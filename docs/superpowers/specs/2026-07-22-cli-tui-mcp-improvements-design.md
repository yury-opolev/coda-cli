# Startup Prompt, Transcript, Tool Summary, and MCP Manager Design

**Date:** 2026-07-22  
**Status:** Approved

## Goal

Improve Coda in four independently testable areas:

1. Accept an exact system prompt as inline text or from a file when starting interactive TUI or serve mode.
2. Complete and harden transcript auto-follow, unread, jump-to-bottom, scrollbar, and timestamp behavior.
3. Add a concise aggregated tool-display mode and make it the TUI default.
4. Make bare `/mcp` open an interactive list/detail manager modeled on `/tasks`.

The implementation must preserve full engine, serve, transcript, audit, and telemetry data. Display policy must not
discard tool inputs, outputs, errors, or identifiers.

## Scope and architecture

This is one coordinated delivery with four isolated components:

- startup prompt source resolution and effective-prompt selection;
- transcript viewport state and navigation chrome;
- immutable per-turn tool activity projection;
- shared MCP management operations plus a Terminal.Gui overlay.

The components share only explicit session, event, and management contracts. They do not require a broad generic UI
framework refactor.

Independent implementation tracks may run in parallel. Each track uses targeted red-green tests. The complete
solution test suite and one strongest-model holistic review run after integration.

## Non-goals

- Add exact system-prompt options to `coda run`.
- Add append-system-prompt flags.
- Add a system-prompt editor or live prompt replacement inside a running session.
- Change project instruction-file discovery.
- Add a global settings UI for tool display mode.
- Add session-only MCP connect/disconnect controls.
- Replace the existing textual `/mcp <subcommand>` interface.
- Generalize all Terminal.Gui overlays before implementing the MCP browser.

## 1. Exact startup system prompts

### Command-line interface

Interactive TUI accepts:

```text
coda --system-prompt <text>
coda --system-prompt-file <path>
```

Serve mode accepts:

```text
coda serve --system-prompt <text>
coda serve --system-prompt-file <path>
```

The two options are mutually exclusive. Missing values, duplicate conflicting sources, or malformed arguments fail
startup with a non-zero exit code and a concise error.

`--system-prompt` is distinct from the existing headless `--prompt`, which remains the user task for `coda run`.

Serve otherwise retains its forward-compatible argument parsing. These two prompt-source options deliberately
validate strictly because silently ignoring an explicit prompt would start a session with unintended instructions.

### Source resolution

A shared CLI-side `SystemPromptSourceResolver` resolves the startup option before creating a TUI session or serve
transport.

Inline text is preserved exactly as received from argument parsing.

File input follows this contract:

- relative paths resolve from the process startup working directory, not from `--cwd`;
- the file is read once at startup;
- UTF-8 and UTF-8 with BOM are accepted;
- invalid UTF-8 is rejected;
- whitespace, line endings, and a trailing newline are preserved;
- missing files, directories, access errors, and read failures are surfaced;
- no trimming, normalization, fallback, or silent truncation occurs.

The resolver returns resolved text. File paths do not flow into the engine and are not reread per turn.

### Exact replacement semantics

The resolved text is the complete root system prompt.

When an override is present, Coda does not add:

- the built-in Coda system prompt;
- project `CLAUDE.md` instructions;
- output-style instructions;
- provider-specific system prefixes.

The exact supplied text is sent to every provider. If a provider rejects it, Coda surfaces the provider error rather
than silently modifying the prompt.

When the selected provider is Claude.ai OAuth, show a non-blocking startup warning that the provider may require its
compatibility prefix. The warning does not alter the prompt or prevent startup.

### Session propagation

Add an optional `SystemPromptOverride` to `SessionOptions`. The value is resolved text, not a source descriptor.

One SDK-level effective-prompt component is used by:

- normal root turns;
- `/context` analysis;
- scheduled root turns;
- audit recording.

This prevents normal turns and context analysis from reporting different prompts.

Delegated subagents retain their agent-specific role prompts. They do not inherit the root's exact override.

### Persistence and resume

Add a new optional persisted `systemPromptOverride` metadata field separate from the effective prompt recorded for
each audit turn. Store it in the live session metadata used by interactive resume and as an additive optional field in
session export/import bundles. Existing `coda.session/1` readers must tolerate the absent or additional optional field.
Fork/carry copies the override with the other explicit session metadata.

Resume precedence is:

1. the CLI resolves and passes a new startup override supplied for the resume;
2. when the CLI passes no new value, the SDK/session loader restores the persisted session override;
3. when neither exists, the SDK uses normal Coda prompt construction.

Existing sessions without override metadata retain current behavior. Normal default prompts are not frozen into
session metadata.

## 2. Transcript follow, unread, and navigation

### Follow state

`TranscriptViewportState` remains the single source of truth for transcript position. Its user-facing state is:

```text
Following
Detached
```

The transcript starts in `Following`.

While following, appended content and streaming growth keep the viewport at the bottom.

The following user actions enter `Detached` when they move away from the bottom:

- mouse wheel up;
- Up/PageUp/Home transcript navigation;
- upward scrollbar click or drag;
- any equivalent viewport movement.

Detached state preserves a stable anchor consisting of transcript block ID and wrapped-row offset. It does not rely
only on an absolute row index, so streaming replacement or width changes do not unexpectedly move the user's view.

The following restore `Following`:

- clicking the jump control;
- pressing `Ctrl+End` while the main shell is active, regardless of composer or transcript focus;
- End or downward navigation that reaches the bottom;
- scrollbar movement that reaches the bottom.

Modal overlays retain their own navigation and do not route `Ctrl+End` to the transcript behind them.

### Unread semantics

While detached, newly appended visible top-level transcript blocks increment the unseen count.

The following do not increment unseen repeatedly:

- streaming replacement of an existing assistant block;
- progress replacement of an existing tool-activity block;
- hidden blocks in `tiny` mode;
- layout-only separator rows.

The first visible insertion of a new tool-activity block while detached increments unseen once. Later updates to that
same block do not.

Reaching or jumping to the bottom clears unseen state atomically.

### Jump control

When detached, show one centered clickable control immediately above the composer and within the transcript chrome:

```text
Jump to bottom (Ctrl+End) v
```

When unseen content exists, replace the label:

```text
3 new messages (Ctrl+End) v
```

The control must not share the status row's final layout cell or depend on z-order overlap. Its rendered rectangle is
the mouse hit target. Inline and fullscreen shells use the same implementation.

### Scrollbar and timestamp clearance

Preserve the interactive right scrollbar:

- track clicks page toward the clicked position;
- thumb dragging updates the viewport continuously;
- mouse capture is always released on button-up, shell teardown, mode switch, or disposal.

Reserve an additional visual gap between right-aligned user timestamps and the scrollbar. The timestamp must not
touch the scrollbar column at any terminal width where both are visible.

## 3. Aggregated tool display mode

### Setting and default

Extend `toolDisplayMode` with:

```json
{
  "toolDisplayMode": "summary"
}
```

Accepted case-insensitive values become:

- `verbose`;
- `compact`;
- `summary`;
- `tiny`.

Missing or invalid values resolve to `summary`. Invalid values continue to warn without blocking startup.

An existing explicit user value remains unchanged. Only the fallback default changes from `tiny` to `summary`.

This supersedes the `tiny` default approved and shipped in
`2026-07-22-dialog-queue-and-transcript-ux-design.md`. The transcript navigation work in this specification builds on
that shipped jump control, unread state, scrollbar, and `Ctrl+End` foundation. It hardens placement/hit-testing,
stable anchoring, timestamp clearance, and teardown rather than replacing the entire implementation.

### Event identity

Tool events require stable correlation. Add identifiers for:

```text
rootTurnId
activityId
callId
sourceId
```

- `rootTurnId` identifies one submitted root user turn through all model/tool iterations until turn completion.
- `activityId` identifies the root turn's aggregate tool activity.
- `callId` uses the provider tool-use ID where available and remains stable through start, progress, and completion.
- `sourceId` distinguishes root and forwarded subagent sources.

Name-based correlation is not sufficient because one turn may repeat the same tool name or receive forwarded activity.

The root turn creates `rootTurnId` when execution starts. The first tool batch derives or creates `activityId`.
`callId` comes from the provider tool-use block and is retained through execution. The forwarding boundary assigns a
stable `sourceId` for the root or subagent source.

Thread the identifiers through engine sinks, SDK recording, subagent forwarding, UI events, and serve DTOs. Serve
tool-event additions are optional additive JSON fields so tolerant existing clients remain compatible. Update the
serve protocol documentation. Recording and forwarding sinks preserve the identifiers.

### Immutable activity model

The UI reducer owns one immutable `ToolActivityTranscriptBlock` per root assistant turn that uses tools.

Conceptually it contains:

```text
blockId
rootTurnId
activityId
calls[]
completionState
```

Each call contains:

```text
callId
sourceId
toolName
input
safePreview
status
elapsed
result
error
```

Call status distinguishes at least:

- pending;
- awaiting approval;
- running;
- succeeded;
- failed;
- cancelled;
- skipped.

The block is inserted at the first tool event in normal transcript order and replaced in place by stable block ID.
All later tool batches in the same root turn update the same block.

Turn interruption finalizes running or approval-waiting calls as cancelled and not-yet-started pending calls as
skipped. Unexpected completion without a known start creates an explicit synthetic/orphan entry or diagnostic; it
never updates another call by tool name.

### `summary` rendering

While active, render a parent line using the cumulative number of invoked calls:

```text
Running 12 tools...
```

Use a specific noun for a homogeneous recognized kind:

```text
Running 3 shell commands...
```

The initial specialized noun mapping is intentionally small: `run_command` maps to `shell command`; all other
homogeneous or mixed activity uses `tool`/`tools`. Additional nouns require their own tests.

Below the parent, render only currently running calls:

```text
|- $ dotnet test --filter TranscriptViewport
|- Reading McpCommand.cs
`- Searching for ToolDisplayMode
```

Child previews are sanitized and single-line. `run_command` shows the command, file tools show the relevant path,
search tools show the query, and all other tools use the existing sanitized input preview. Never expose unredacted
secrets.

Render at most five child rows. If more are running, reserve the final row for:

```text
`- ...and N more
```

Completed, failed, skipped, and pending calls remain in immutable state for totals but disappear from the live child
list.

At root turn completion, collapse the activity block to exactly one line:

```text
Ran 12 tools
Ran 3 shell commands
Ran 12 tools - 1 failed
Ran 4 tools - cancelled
```

Mixed tool kinds use `tools`. Failure and cancellation suffixes remain concise.

### Other display modes

The same immutable activity block projects differently by mode:

- `verbose`: all calls, complete inputs, progress, results, and errors;
- `compact`: one sanitized status line per call without complete result content;
- `summary`: aggregate live state and one completed line;
- `tiny`: no visible tool transcript block.

Display mode never changes persisted history, serve events, audits, task logs, telemetry, or engine behavior.

### Plain output and resume

Plain append-only output cannot retract live child rows. In `summary`, it suppresses start/progress rendering and
prints only the final aggregate line.

Resume/history projection groups all tool calls belonging to one historical root turn into one completed activity
block. It does not reconstruct one block per invocation.

## 4. Interactive MCP manager

### Opening behavior

Exact bare `/mcp` in Terminal.Gui opens an overlay before normal slash-command dispatch, matching `/tasks`.

The overlay may open while an agent turn is active. Browsing remains available, but mutations are disabled until a
safe turn boundary so the active tool registry cannot change mid-turn.

Existing commands remain:

```text
/mcp list
/mcp info
/mcp add
/mcp edit
/mcp remove
/mcp enable
/mcp disable
/mcp start
/mcp stop
/mcp restart
```

Plain and non-Terminal.Gui modes keep textual behavior. Bare `/mcp` there remains equivalent to textual list output.

### Navigation

The overlay mirrors `/tasks` list/detail navigation.

List screen:

- Up/Down, PageUp/PageDown, Home/End: navigate;
- Enter: open the selected server's detail screen;
- `a`: add;
- `e`: edit, including rename;
- Space: enable or disable;
- `u`: reauthenticate;
- Delete: delete;
- Esc: close.

Detail screen:

- `e`: edit name and settings;
- Space: enable or disable;
- `u`: reauthenticate;
- Delete: delete;
- Esc: return to the list.

Add and Edit open a dedicated full-overlay editor view owned by `McpBrowserState`. Tab/Shift+Tab move between fields,
Enter applies the focused action, and Esc cancels back to the originating screen. Printable editor input never
triggers list/detail actions.

Add begins with an explicit User or Project scope selection, defaulting to Project when a project configuration is
available and User otherwise.

### List model

Show every physical user and project definition as a separate row, even when names collide.

Each row includes:

- server name;
- scope;
- persisted enabled/disabled state;
- live connected/error state;
- effective or overridden status.

Selection identity is `(scope, name)`, not name alone.

The MCP configuration layer adds a physical-definition read model without changing the existing effective merged
load used by runtime startup.

### Detail model

The detail screen shows:

- name and scope;
- source file;
- enabled state;
- connected/error state;
- effective or overridden state;
- stdio command/arguments or HTTP URL;
- redacted environment, headers, and authentication references;
- available tools, prompts, and resources;
- last connection or authentication error.

Resolved secret values are never rendered or placed in UI events.

### Shared management service

Extract a shared `McpManagementService` from private command handlers. Text commands and the overlay call the same
operations for:

- add;
- edit and rename;
- enable and disable;
- reauthenticate;
- delete;
- runtime reconciliation;
- refresh.

The service owns validation, scope resolution, atomic writes, secret migration/cleanup, runtime changes, and event
publication. UI controller code does not manipulate JSON or credential storage directly.

### Edit and rename

Edit includes the name field. It is a pre-populated incremental edit, not a replacement wizard.

Editing:

- preserves the current disabled state unless the user changes it;
- preserves unknown/unmodified fields where the writer supports them;
- rejects blank names, control characters, path separators, and same-scope collisions;
- warns when a new name will override or reveal another scope;
- validates transport-specific fields before writing;
- never pre-populates decrypted secrets;
- uses an explicit unchanged/replace/remove choice for managed secret fields.

Rename updates the config entry and credential references as one logical transaction. Obsolete secret keys are
deleted only after a successful atomic config write. A failed write leaves the old entry and secrets intact.

After a successful edit or rename, reload effective configuration and restart the effective server immediately when
enabled. If reconnection fails, keep the saved configuration and show the server as disconnected with the real error.

### Enable and disable

There is no separate session-only disconnect action.

Enable/disable changes persisted configuration and reconciles live runtime:

- enabling starts the server immediately when it is effective;
- disabling stops the effective server and removes its live tools immediately;
- changing an overridden definition updates persistence but does not replace the currently effective runtime.

A disabled higher-precedence definition continues to shadow same-name lower-scope definitions. Disable therefore does
not reveal or start a lower-scope server; deleting the higher-precedence definition does.

### Reauthenticate

Reauthenticate is available from list and detail screens when the selected server has managed authentication.

- OAuth: confirm, clear/replace stored OAuth state, run authentication, then reconnect.
- Credential-store bearer/header secret: confirm, prompt with masked input, replace the stored value, then reconnect.
- Environment-owned credentials: explain that authentication is externally managed and do not rewrite the variable.
- No authentication: disable the action with a clear reason.

Confirmation occurs before replacing existing managed credentials.

### Delete

Delete is available from list and detail screens and always requires confirmation. The confirmation identifies the
server name, scope, and any cross-scope effect.

After a successful atomic config write:

- stop/remove the deleted effective runtime;
- delete only secrets no longer referenced;
- reload physical and effective definitions;
- if deleting an override reveals an enabled lower-scope definition, start that definition;
- return to the list with stable nearest selection and a status message.

A write failure leaves runtime and secrets unchanged.

### Components

- `McpBrowserState`: immutable list/detail/editor state, stable `(scope, name)` selection, busy/error/status state.
- `McpBrowserKeyMap`: pure view-dependent key-to-intent mapping.
- `McpBrowserController`: loads physical/effective state, serializes actions, enforces turn-boundary mutation rules,
  and refreshes after changes.
- `McpBrowserOverlay`: Terminal.Gui rendering, focus, and input routing only.
- `McpManagementService`: shared validated config, secret, authentication, and runtime operations.

The shell owns overlay construction, z-order, focus restoration, and disposal, following the existing task-browser
pattern.

## 5. Error handling, concurrency, and lifecycle

- Prompt source failures stop startup before a session or transport becomes visible.
- Transcript state transitions and unread clearing are atomic within the UI actor/reducer path.
- Tool progress coalesces by call ID, never by tool name.
- Tool activity completion is a critical UI event and cannot be dropped behind progress events.
- MCP mutations are serialized and run only at safe turn boundaries.
- MCP validation completes before writes; runtime reconciliation follows a successful write.
- Config write failure produces no success message, runtime mutation, or secret deletion.
- Runtime restart failure preserves saved configuration and exposes disconnected/error state.
- Overlay close, shell mode switch, and shutdown dispose subscriptions, cancel work, and release mouse capture.
- Inline and fullscreen shells share behavior rather than maintaining parallel state machines.

## 6. Testing

### Exact system prompt

- interactive and serve parsing;
- missing values and mutual exclusion;
- coexistence with resume/fork/startup flags;
- inline text exact equality;
- UTF-8, BOM, Unicode, line endings, and trailing newline preservation;
- invalid UTF-8, missing file, directory, and access errors;
- normal prompt fallback when absent;
- exclusion of built-in, project, output-style, and provider prefixes when present;
- `/context` parity;
- scheduled root propagation;
- subagent prompt isolation;
- persistence, resume precedence, export/import, and fork behavior.

### Transcript navigation

- Following to Detached transitions for every input path;
- append while following;
- stable detached anchor during streaming and reflow;
- unseen increment and non-increment cases;
- click hit-testing for both jump labels;
- `Ctrl+End` across main-shell focus targets;
- manual bottom arrival and atomic unseen reset;
- timestamp-to-scrollbar gap;
- scrollbar track, thumb drag, release, teardown, and mode switch;
- inline/fullscreen parity.

### Tool summary

- new setting value and fallback default;
- explicit old settings remain unchanged;
- same-name and repeated calls correlate by ID;
- multiple tool batches aggregate into one root-turn block;
- root and forwarded source identity;
- pending, approval, running, success, failure, skipped, and cancellation transitions;
- one through five live children and truncation beyond five;
- homogeneous and mixed-kind wording;
- completed failure/cancellation suffixes;
- turn interruption finalization;
- orphan completion handling;
- streaming block replacement without unread inflation;
- plain final-only rendering;
- resume/history grouping;
- serve serialization compatibility.

### MCP manager

- exact bare-command interception and textual fallback;
- list/detail navigation and focus restoration;
- physical user/project rows and effective/overridden markers;
- stable `(scope, name)` selection;
- busy-turn read-only behavior;
- add and pre-populated edit;
- edit rename, collisions, disabled-state preservation, and cross-scope warnings;
- masked unchanged/replace/remove secret handling;
- secret-reference migration and cleanup;
- immediate runtime enable/disable and edit restart;
- disabled higher-precedence definitions continue to shadow lower scope;
- restart failure state;
- OAuth, stored-secret, environment-owned, and no-auth reauthentication paths;
- delete and reauthentication confirmation;
- deleting an override reveals/restarts lower scope;
- write-failure rollback behavior;
- control/ANSI sanitization;
- overlay disposal and refresh;
- existing textual command compatibility.

## 7. Delivery and review

Implementation is decomposed into parallel tracks where files do not overlap:

1. exact prompt source, session propagation, and persistence;
2. transcript navigation fixes;
3. correlated tool activity model and `summary` rendering;
4. MCP management service and physical read model;
5. MCP overlay integration.

Each track uses the smallest targeted test command that proves its behavior. Focused review is reserved for risky
contracts such as session persistence, tool-event correlation, secret migration, and runtime reconciliation.

After integration:

1. update CLI help, README, settings documentation, and serve documentation;
2. run the complete solution test suite;
3. use the strongest available model for one holistic code review;
4. fix review findings and rerun affected tests plus the complete suite;
5. issue a final spec-compliance and maintainability verdict.
