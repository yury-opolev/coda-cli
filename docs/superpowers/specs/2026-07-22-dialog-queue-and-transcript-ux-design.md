# Dialog, Queue, and Transcript UX Design

**Date:** 2026-07-22  
**Status:** Approved

## Goal

Improve Coda's interactive experience in five related areas:

1. Separate every visible transcript block with one blank row.
2. Make tool-call detail user-configurable, with quiet output as the default.
3. Make Shift+Enter insert a newline reliably in Windows Terminal.
4. Deliver queued steering messages at the earliest safe boundary and make them visible and recallable.
5. Add clear scroll-position, unread-message, jump-to-bottom, and scrollbar affordances.

The implementation must preserve full engine, serve, transcript, audit, and telemetry data even when the
interactive renderer hides details.

## Scope and architecture

This is one cohesive change with bounded components:

- transcript layout owns separators and navigation chrome;
- transcript formatting owns tool-detail projection;
- user settings own the global display policy;
- a shared engine queue owns pending steering state;
- TUI and serve adapt the shared queue instead of maintaining parallel queues;
- terminal startup owns Windows Terminal input compatibility.

The work ships together and receives one holistic code review after all implementation groups are integrated.

## 1. Transcript block spacing

Every visible semantic transcript block receives exactly one unstyled blank row after it:

- user messages;
- assistant responses;
- tool calls and results;
- permission requests;
- user questions;
- notices, warnings, and errors;
- diffs;
- command output;
- context-usage output;
- session boundaries;
- pending queued messages.

`TranscriptLayoutIndex` owns the separator row. Individual block formatters do not append it.

This keeps the behavior uniform and ensures:

- a separator never inherits a user block's full-width background;
- a separator is not selectable as part of an expandable tool or diff block;
- hidden tool blocks leave no empty gaps;
- internal Markdown paragraph spacing remains unchanged;
- inline and fullscreen modes behave identically;
- streaming replacement and incremental row counts remain correct.

Only blocks that produce at least one visible render row receive a separator.

## 2. Tool display policy

### Setting

Add a user-level setting:

```json
{
  "toolDisplayMode": "tiny"
}
```

Accepted values are case-insensitive:

- `verbose`;
- `compact`;
- `tiny`.

Missing or invalid values resolve to `tiny`. Invalid values produce a warning through the existing logging
path without preventing startup.

The value is read exclusively from the user settings file:

```text
~/.coda/settings.json
```

`CODA_SETTINGS_DIR` continues to relocate the user settings root for tests and managed hosts. A project's
`.coda/settings.json` cannot override this setting. Manual edits take effect on the next Coda launch; no slash
command, settings dialog, or live file watcher is added.

### Modes

#### `verbose`

Preserve the current tool presentation:

- tool name;
- complete input JSON;
- running duration/state;
- complete result content;
- error state.

#### `compact`

Render:

- tool name;
- sanitized single-line input preview capped at 128 characters;
- running, success, or error state.

Do not render tool result content. Truncation affects display only and never mutates the stored event or
transcript block.

#### `tiny`

Render no tool transcript blocks. This is the default.

The operational status remains visible but uses a generic `Working` label instead of exposing the active tool
name.

### Data preservation

The display setting affects interactive human rendering only:

- retained TUI;
- plain interactive fallback output.

It does not suppress or truncate:

- serve JSON-RPC tool events;
- engine events;
- persisted conversation history;
- session bundles or audits;
- task logs;
- telemetry.

Tool events remain in `UiSessionSnapshot`. Rendering applies the current presentation policy, so display
filtering does not become an engine concern.

## 3. Shared queued-steering model

### Queue primitive

Replace the string-only steering inbox behavior with an ID-bearing shared queue. Each entry contains:

```text
id
text
enqueuedAt
```

The queue exposes atomic operations:

- `Enqueue` returns the accepted entry;
- `TakeAllForDelivery` removes and returns every pending entry in FIFO order;
- `RecallAll` removes and returns every pending entry in FIFO order;
- `TrySealEmpty` atomically closes an empty queue at the end of a turn/task;
- `Clear` removes pending entries at a session/task boundary.

Delivery and recall are mutually exclusive for each entry. A race may choose one operation, but it cannot split,
duplicate, or return the same entry twice.

The queue is open only while its owning turn/task can still consume steering. Final turn completion uses
`TrySealEmpty` rather than a separate "check empty, then close" sequence:

- if the queue is empty, it closes atomically and the turn may complete;
- if a late message is already pending, sealing fails, the message is delivered, and the turn continues;
- enqueue after sealing is rejected rather than accepted and lost.

In the TUI, a busy submission that loses this close race is retained as the next normal prompt and dispatched as
soon as the finishing turn releases the dispatcher. Serve reports the steer as not accepted, preserving its
existing retry-as-`session/prompt` contract. Task steering returns the existing invalid-state result after a task
seals.

The same primitive is used by:

- the main interactive session;
- serve-mode sessions;
- foreground subagents;
- background subagents;
- scheduled agents.

### Earliest safe delivery boundary

Queued messages are delivered at the first boundary where provider history can remain valid.

#### Before a model request

If messages are already pending, inject all of them FIFO as one synthetic user message before the request. Join
their text with one blank line between entries.

#### During model streaming

Finish the current model response. If it requested tools, do not start them. Produce explicit skipped results for
every not-yet-started tool and inject the queued messages before the next model request.

#### During a tool call

Finish the currently running tool. Do not start later sequential tool calls from the same assistant response.
Produce explicit skipped results for every remaining call, then inject the queued messages before the next model
request.

#### At a natural text-only stop

If the model produced no tools and would otherwise complete the turn, pending messages keep the same turn alive.
Inject them and issue another model request.

### Tool-use history validity

Every emitted provider `tool_use` receives a corresponding `tool_result` in the immediately following user
message.

Skipped tools use an error-shaped result that states they were not executed because new operator steering arrived.
The result is explicit and deterministic; it is not reported as a successful execution.

The user message after an interrupted tool batch contains:

1. the completed current tool result, when one ran;
2. skipped results for every remaining tool;
3. the combined steering text.

### Delivery notification

The agent sink gains a delivery notification carrying the delivered entry IDs. Forwarding sinks preserve it like
other lifecycle events.

The TUI uses the notification to convert pending rows in place. Serve emits a corresponding additive event so an
orchestrator can reconcile pending UI state.

## 4. TUI queued-message behavior

### Submit while busy

Ordinary non-command submission during an active turn is queued instead of dropped.

Existing live permission commands remain on their separate serialized side-band path. Empty submissions, startup
submissions, and submissions after exit is requested remain rejected.

Each accepted message creates a pending transcript block with:

- the queue entry ID;
- the original text;
- the enqueue time;
- a visible pending status.

Pending messages use normal user-message styling plus a distinct pending annotation.

### Delivery

When the engine reports delivered IDs, matching pending blocks convert in place to normal user-message blocks:

- preserve block identity, text, and time;
- remove the pending marker;
- do not append a duplicate user block;
- preserve transcript scroll and selection stability.

### Recall with Up

Up recalls queued messages only when:

- at least one message is pending;
- the composer is empty;
- the caret is on the first visual line;
- command completion and modal prompts do not own the key.

Recall:

1. atomically removes all still-pending entries from the shared queue;
2. removes their pending transcript blocks;
3. joins their text with blank lines;
4. restores the result into the composer;
5. places the caret at the end.

If delivery wins a race, delivered entries are not recalled. If recall wins, the engine cannot later deliver those
entries.

Queued state survives inline/fullscreen mode switches because it belongs to the session queue and semantic
snapshot, not a shell instance.

Turn interruption and session/task teardown clear only entries that are still pending; delivered user blocks
remain visible.

## 5. Serve and task APIs

### Serve

Keep `session/steer` backward-compatible and add the accepted queue entry ID to its result.

The additive result fields are:

```json
{
  "ok": true,
  "messageId": "queue-entry-id"
}
```

Add:

```text
session/recallSteering
```

The request has no parameters. Its result contains the ordered pending-message array and atomically clears those
entries:

```json
{
  "messages": [
    {
      "id": "queue-entry-id",
      "text": "correct the parser first",
      "enqueuedAt": "2026-07-22T04:00:00Z"
    }
  ]
}
```

Recall while no entries are pending succeeds with an empty array. Existing authentication and running-session
guards continue to apply.

Add an event reporting delivered entry IDs so orchestrators can update pending state without polling.

The event name is:

```text
event/steeringDelivered
```

Its payload is:

```json
{
  "messageIds": ["queue-entry-id"]
}
```

### Tasks and subagents

`TaskManager` exposes an authorization-aware recall operation with the same visibility rules as steering:

- the main agent may recall from any running steerable task;
- a task may recall only from its running descendants;
- unauthorized and unknown targets remain indistinguishable;
- shell tasks remain non-steerable.

Add a dedicated task recall tool rather than overloading `task_send`. It returns the ordered recalled-message array.
The tool is named `task_recall` and is available through the same deferred-tool and task registry paths as the
existing task tools.

## 6. Shift+Enter in Windows Terminal

### Existing behavior retained

The composer action map continues to define:

- Shift+Enter: insert newline;
- Ctrl+Enter: insert newline;
- Ctrl+J: insert newline;
- plain Enter: submit.

Bracketed paste remains literal and cannot submit.

### Compatibility boundary

The existing widget tests synthesize `Key.Enter.WithShift` and therefore do not cover the real terminal decoder.
The fix belongs below `ComposerView`.

Add a terminal-input compatibility component used during Terminal.Gui startup:

1. detect Windows Terminal through its host environment;
2. prefer the Terminal.Gui ANSI input path for that host so Terminal.Gui 2.4.17 can negotiate and parse the Kitty
   keyboard protocol;
3. normalize supported modified-Enter variants to `Key.Enter.WithShift`;
4. pass native `Key.Enter.WithShift` through unchanged;
5. preserve the default driver path outside supported Windows Terminal sessions.

Terminal.Gui 2.4.17 already contains Kitty protocol detection and `CSI 13;2u` parsing. Do not upgrade the package
unless implementation proves the pinned version cannot expose the required path safely.

Capability detection or protocol negotiation failure must not crash or delay startup indefinitely. Ctrl+Enter and
Ctrl+J remain documented fallbacks.

### Validation boundary

Add a decoder/driver-level test that feeds the real modified-Enter representation through the selected input path
and proves it becomes the existing newline action. Keep the existing action-map and composer tests as regression
coverage.

## 7. Transcript navigation chrome

### Jump-to-bottom hint

Replace the current header-only unseen-row text with a floating one-row hint anchored at the bottom of the transcript
panel.

When scrolled away with no unseen messages:

```text
Jump to bottom (Ctrl+End) ↓
```

When visible transcript blocks arrive:

```text
1 new message (Ctrl+End) ↓
2 new messages (Ctrl+End) ↓
```

The count tracks newly appended visible transcript blocks, not wrapped rows:

- streaming growth of an existing block does not increment it;
- hidden tool blocks in `tiny` mode do not increment it;
- singular/plural wording is correct;
- jumping to the bottom clears it immediately.

The hint:

- is hidden while auto-following;
- uses the Warm Ember theme with a contrasting dark background;
- is centered horizontally when space permits and safely truncated on narrow terminals;
- stays above transcript content and below modal overlays;
- is clickable and jumps to the bottom.

Ctrl+End becomes a shell-global jump action, including while the composer has focus. Existing transcript-focused
Ctrl+End behavior remains equivalent.

### Interactive scrollbar

Reserve one cell at the transcript's right edge whenever content exceeds the viewport. Transcript wrapping,
selection, timestamps, and drawing use the remaining width.

Draw:

- a dim vertical track;
- a contrasting thumb;
- a minimum one-row thumb;
- a full-height thumb when all content fits.

Thumb size and position derive from:

```text
content rows
viewport height
top visible row
```

All calculations clamp for empty content, one-row viewports, resize, reflow, and content shrinkage.

Mouse behavior:

- clicking above or below the thumb pages in that direction;
- dragging the thumb continuously maps pointer position to `TopRow`;
- releasing the mouse ends the drag and releases capture;
- reaching the bottom restores auto-follow and clears unseen-message state.

Mouse-wheel and keyboard scrolling remain unchanged. `--no-mouse` keeps the scrollbar visible but disables its
interactive behavior.

Inline and fullscreen modes use the same hint and scrollbar implementation.

## 8. Error handling and lifecycle

- Queue operations are synchronized and never silently duplicate or lose an entry through a delivery/recall race.
- Unknown or unauthorized task targets preserve existing non-disclosure behavior.
- Failed skipped-tool construction is treated as an engine error rather than emitting malformed provider history.
- Tool-display filtering never mutates source blocks or wire events.
- Invalid tool-display values warn and fall back to `tiny`.
- Input compatibility failure preserves fallback shortcuts and the existing TUI fallback path.
- Scrollbar and hint handlers release mouse capture and unsubscribe during shell disposal and mode switches.
- Resize and reflow preserve a valid clamped viewport.
- Pending-row conversion and removal preserve stable block IDs for unaffected rows.

## 9. Testing

### Transcript and tool display

- Every visible block type receives exactly one trailing separator.
- Hidden tool blocks receive no separator.
- `verbose` preserves full input and result content.
- `compact` caps the argument preview at 128 characters and shows no result content.
- `tiny` renders no tool rows and does not expose tool names in operational status.
- Missing, mixed-case, invalid, user-only, and attempted project-override settings behave correctly.
- Plain interactive fallback follows the selected mode.
- Serve, audit, telemetry, and persisted history remain complete.

### Queue engine

- FIFO enqueue and delivery.
- Atomic recall-all.
- Delivery-versus-recall race returns each entry exactly once.
- Pending messages before a model request are injected before sampling.
- Messages arriving during streaming skip all not-yet-started tools.
- Messages arriving during tool N finish N and skip N+1 onward.
- Every skipped `tool_use` receives an error-shaped `tool_result`.
- Text-only natural stop continues when steering is pending.
- Main sessions, subagents, and scheduled agents share behavior.
- Turn/task cleanup does not leak stale pending messages.

### TUI queue behavior

- Busy submissions enqueue instead of being dropped.
- Live permission commands retain their side-band behavior.
- Pending rows appear immediately.
- Delivery converts rows in place without duplication.
- Up recall obeys composer/completion/prompt precedence.
- Recall-all joins entries with blank lines and restores the caret.
- Mode switches preserve pending state.
- Interrupt and teardown remove only still-pending rows.

### Serve and tasks

- Steering results include entry IDs without breaking existing deserialization.
- Serve recall returns ordered entries and clears them.
- Empty recall succeeds.
- Delivery events round-trip.
- Task recall follows authorization, task-kind, and run-state rules.
- Deferred tool discovery includes the new task recall tool.

### Windows Terminal input

- Native Shift+Enter passes through.
- `CSI 13;2u` becomes Shift+Enter through the selected decoder path.
- Ctrl+Enter and Ctrl+J continue to insert newlines.
- Plain Enter continues to submit.
- Unsupported terminals retain fallback behavior.

### Navigation chrome

- The jump hint appears whenever auto-follow is off.
- It switches between `Jump to bottom` and singular/plural unseen-message text.
- Streaming row growth does not inflate the message count.
- Hidden tools do not increment the count.
- Ctrl+End works from transcript and composer focus.
- Clicking the hint jumps and clears unseen state.
- Scrollbar thumb math is correct at top, middle, bottom, resize, and short content.
- Track clicks, paging, dragging, release, and `--no-mouse` behavior are correct.
- Text wrapping and selection never overlap the reserved scrollbar column.

## 10. Implementation organization

The implementation plan should use these groups:

1. **Rendering foundation:** separators, user-only setting, tool modes, tiny-mode status.
2. **Queue engine and APIs:** shared queue, safe-boundary delivery, skipped results, serve/task recall.
3. **Viewport UX:** message-counted hint, global Ctrl+End, clickable hint, interactive scrollbar.
4. **Input compatibility:** Windows Terminal reproduction, driver selection/normalization, decoder-level tests.
5. **TUI integration:** busy enqueue, pending rows, delivery conversion, Up recall, lifecycle wiring, docs.

Groups 1-4 are independently implementable and may run in parallel. Group 5 integrates their shared seams.

Use fast implementation-capable models such as GPT-5.6 Luna or GPT-5.6 Terra for the implementation groups. Do
not perform separate review passes after each group. Once all changes are integrated and verified, use the
strongest available model for one holistic code review and one final verdict.

## Acceptance criteria

1. Every visible transcript block has exactly one blank row after it.
2. `toolDisplayMode` is user-global, defaults to `tiny`, and supports the approved three projections.
3. Shift+Enter inserts a newline in supported Windows Terminal sessions through the real input path.
4. Busy TUI submissions become visible pending messages instead of being dropped.
5. Queued messages are delivered immediately after the current safe boundary, including between sequential tools.
6. Remaining not-yet-started tools are explicitly skipped with provider-valid results.
7. Up recalls every still-pending TUI message into one composer draft.
8. Serve and task callers can atomically recall ordered pending messages.
9. Pending rows convert to normal user rows when delivered.
10. Scrolled-away transcripts show the approved jump/unseen hint and a global Ctrl+End shortcut.
11. The transcript has an interactive right-side scrollbar in inline and fullscreen modes.
12. Quiet rendering never removes data from engine, serve, history, audit, task-log, or telemetry surfaces.

## Out of scope

- A general `/settings` UI.
- A slash command for changing tool display mode.
- Live reloading of `settings.json`.
- Per-project tool-display overrides.
- Per-tool display policies.
- An end-of-turn-only follow-up queue distinct from immediate steering.
- Replacing Terminal.Gui's complete input stack.
- Changing serve transport framing or protocol version.
- A general mailbox refactor for prompts, tools, and agent events.
