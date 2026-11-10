# Task titles and compact completion cards — design

Date: 2026-11-10 · Request: "REQUEST CODA titles" (email, 2026-11-10) · Base: main 3ce7cd4

## Problem

A finished background task is announced in the TUI as the notice
`Task {status}: {description}`, and for a subagent the description *is* its
whole prompt (`SubagentHost::spawn` registers `request.prompt`). Long
instructions flood the transcript, hide the result and make concurrent tasks
hard to tell apart. Tasks have no short identity label: `TaskTool` takes only a
prompt, `TaskSnapshot` only a description.

## Decisions (made autonomously; the requester was unavailable)

| # | Decision | Why |
|---|----------|-----|
| D1 | A task title is a validated, single-line label of **at most 80 Unicode scalar values** after trimming surrounding whitespace. | 3–8 words fit comfortably; 80 is a common one-line bound and is still small next to a prompt. |
| D2 | Rejected: missing, empty, whitespace-only, over 80 characters, any `char::is_control` character (covers `\n`, `\r`, `\t`, ESC, DEL, C1), U+2028/U+2029, and bidi embedding/override/isolate controls (U+202A–U+202E, U+2066–U+2069). | Single line, no terminal escapes, no visual reordering; accepted text renders literally. |
| D3 | Validation lives in one function, `coda_agent::tasks::validate_task_title`, and is enforced by `TaskManager::register`, which now takes a `title` argument and rejects an invalid one **before** an id is issued or anything is registered, logged or announced. | One gate every creation path must pass; the compiler forces every producer to supply a title. |
| D4 | The model supplies the title for subagents: the `task` tool (and its deprecated alias `task_start`) gains a **required** `title` property (`maxLength: 80`). `start_subagent` validates it before any factory call, depth check or registration, with an actionable error naming the rule and an example. | "The spawning agent supplies the title with the existing task invocation." No extra model call. |
| D5 | `SubagentRequest` carries `title`. `SubagentHost::spawn` (background) passes it to `register`, so a programmatic caller that omits it gets the same validation error. Foreground requests do not register a task and ignore it. | Programmatic entry points cannot bypass the gate. |
| D6 | Internal producers derive a deterministic label with `derive_task_title(text)` (first non-empty line, controls → spaces, whitespace collapsed, elided to 80 chars with `…`): shell tasks from the command (`run_command` is a command runner whose task record is an internal detail of the non-blocking runtime — its schema is unchanged), tool tasks from the tool name, scheduled runs from `Scheduled: <name or prompt>`. The derived label is then validated by `register` like any other title. | "Internal producers supply an appropriate label at creation." A command line is already a concise label; forcing a second argument on every shell call would change the tool contract for every command. |
| D7 | The description/prompt is stored unchanged and separately. Agents receive exactly the prompts they did before. | Scope rule. |
| D8 | `ManagedTask`/`TaskSnapshot`/`TaskCompletionEntry` carry `title: Option<String>`. `None` exists **only** for a task restored from a pre-title checkpoint (`register_restored(.., title: Option<&str>, ..)`); an invalid stored title is treated as absent (legacy display), never rewritten. | Legacy is a read path, never a creation path. |
| D9 | Wire: `event/taskStarted`, `event/taskCompleted` and `session/listTasks` rows gain an optional `title` (omitted when absent). `event/taskCompleted.description` keeps carrying the full description, so a recorded completion is self-contained. Subagent checkpoint files gain optional `title` (format version unchanged: an absent field reads as legacy). | Additive, backward compatible for readers. |
| D10 | Model-facing lists reuse the title: `task_list` rows, the live-task brief, the side-question snapshot and the completion frame label show the title when present (else the description as before); `task_get` adds a `title:` line and keeps `description:`. | "Reuse it in task lists and lifecycle notifications." |
| D11 | TUI: a new client-owned `Block::TaskCompletion { session_id, task_id, status, kind, title, instructions }` replaces the flat notice. Suppression rules are unchanged (foreground mode or a non-main owner → nothing). A second completion for the same session+task updates the existing card in place. | Structured, task-associated; survives rehydration because client-owned blocks do. |
| D12 | Expansion state is a set of `(session id, task id)` in `Transcript`, not a block index; `replace_conversation` keeps it, `clear` drops it. | Stable across streaming, refresh, rehydration, resize, duplicate titles, multiple sessions. |
| D13 | Collapsed titled card: one row, `▸ Task {status}: {title}  [{task-id}]` built with the shared disclosure header (marker excluded from copy). Width is measured in display cells; the title is elided with `…`; when even that does not fit, the title is dropped to keep `Task {status} [{id}]`, and only then is the whole row truncated. Failed uses the error role, but the status word is always present. | Status and identity win on narrow terminals; never colour-only. |
| D14 | Expanded card: header with the expanded marker, a row `Instructions`, then the full description wrapped literally (sanitised per line, preformatted wrap so long unbroken text is split by cells). | "explicitly labeled Instructions". |
| D15 | Legacy card (no title): header `Task {status}  [{id}]`, plus at most two preview rows of the description (whitespace collapsed, wrapped by cells, the second row ending in `…` when anything is omitted) — at most three rows collapsed. Expanded shows the same Instructions section. | Request §3. |
| D16 | Results/errors are not put in the card (no duplication of the report); they remain in the assistant output and in `/tasks all` → Enter (report section), unchanged. | "Do not duplicate a task's final report into its instructions card." |
| D17 | Mouse: a click on the card's header row toggles it through the reducer (`UiEvent::TaskCardFoldToggled`), exactly like reasoning folds; clicks/drags on body rows start a selection. The existing viewport anchor (block index + row within block) keeps the reader's position when toggling; a new card arriving does not touch other cards' state. | Existing fold behaviour. |
| D18 | Keyboard: `/tasks all` then Enter on a task opens details that now show `title` and an `Instructions` section with the full description (then the report and output as before). The list column becomes `title` (legacy rows fall back to the description's first line). The `/tasks` help summary names the route. The composer Enter key is untouched. | Request §2 keyboard route. |

## Architecture / data flow

```
task tool (title required) ─┐
run_command (derived) ──────┤
tool task (derived) ────────┼─> TaskManager::register(kind, title, description, ..)
scheduled run (derived) ────┤      └─ validate_task_title ─ Err → nothing registered
SubagentHost::spawn ────────┘
ManagedTask{title} → TaskSnapshot{title} → task_list/task_get/live brief/completion frame
                  → SubagentCheckpoint{title?} → register_restored(title?)
                  → TaskEventPublisher → event/taskStarted|taskCompleted{title?}
                  → session/listTasks rows{title?}
TUI: event/taskCompleted → Block::TaskCompletion (client-owned) → render by width
     click header → TaskCardFoldToggled → Transcript.expanded_task_cards
     /tasks all → Enter → details (title, Instructions, report, output)
```

## Error messages

- Missing/blank: `Missing required 'title': give the task a short single-line label (3-8 words, at most 80 characters), for example "Review authentication flow".`
- Too long: `Invalid task title: it is N characters; the limit is 80. Use a short label (3-8 words) and keep details in the prompt.`
- Control/line-break: `Invalid task title: it must be a single line without control characters.`

`TaskManager::register` returns the same text as `Invalid task title: …`.

## Testing

Engine: validation table (missing, blank, whitespace, newline, tab, ESC,
U+2028, bidi, 80 vs 81 chars incl. multi-byte/wide glyphs, trimming);
`register` rejects before allocating an id; task tool rejects missing/blank/
multiline titles without spawning; background host spawn rejects an untitled
programmatic request without registering; title flows into snapshot,
checkpoint round-trip, legacy checkpoint without title restores with
`title: None`; `derive_task_title`. Wire: event round-trip with/without title,
publisher emits title, listTasks row. TUI: titled long multiline prompt →
one collapsed row; narrow widths (status/id kept); wide glyphs / long unbroken
text; legacy ≤ 3 rows with ellipsis; expansion shows `Instructions` + full
prompt; toggle keyed by session+task survives rehydration and an interleaved
second completion; duplicate titles are distinct cards; copy of a collapsed
card excludes hidden instructions and the marker; click on header toggles,
click on body does not; foreground/child suppression unchanged; `/tasks`
details show title + Instructions; removed-task card still expands.

## Out of scope

Changing prompts given to agents, truncating stored prompts, generating
titles with a model, scheduling/execution behaviour, user-message rendering.
