# Tasks TUI and Serve-Parity Design

## Scope

Add a live `/tasks` browser for Terminal.Gui, textual fallback for plain/Spectre modes, foreground shell attachment, and non-UI task lifecycle tools shared with serve mode.

## Layout

`/tasks` opens a focused full-overlay list immediately, without interrupting an active agent turn.

The list shows:

- active tasks first as a parent/child hierarchy;
- recent completed, failed, and stopped tasks below;
- status glyph, description, task kind, foreground/background mode, and duration;
- stable selection by task ID.

Enter opens a focused detail page with metadata, recent output, persistent log path, auto-follow, scrolling, and context-sensitive actions.

## Controls

### List

- Up/Down, PageUp/PageDown, Home/End: navigate.
- Enter: open detail.
- `x` twice within 1.5 seconds: stop a running task.
- `r`: dismiss a terminal task from in-memory history; persistent log remains.
- Esc: close.

### Detail

- `s`: steer a running subagent; hidden/disabled for shells and terminal tasks.
- `a`: attach a background shell's output view and pause the main agent.
- Ctrl+B: release the UI attachment, or detach an originally foreground shell.
- `x` twice: stop.
- `r`: dismiss terminal task.
- `l`: toggle recent live output and persistent log tail.
- End: jump to newest and restore auto-follow.
- Esc: return to list; if attached or still pausing, cancel/release the attachment and resume the main agent first.

## Steering

The main session and subagents use the same `SteeringInbox` mechanism, with separate queue instances.

The steering editor is modal: printable keys cannot trigger task actions. It supports Enter to queue, Shift+Enter/Ctrl+Enter/Ctrl+J for newlines, and Esc to cancel. Messages are delivered FIFO at the subagent's next safe agent-loop boundary as synthetic user input. The page remains open and reports the real manager result (`steering queued`, terminal, denied, or not found).

## Foreground Shell Attachment

Viewing detail does not pause work. Explicit attach:

1. marks the shell `UI attached` in controller-local presentation state without changing its `TaskSnapshot` or original `TaskExecutionMode`;
2. disables the main composer;
3. requests a pause through a reference-counted `AgentExecutionGate`;
4. if the main agent is idle, satisfies the pause immediately;
5. if a turn is active, waits for the next safe loop boundary or turn completion, whichever happens first;
6. shows `pausing main agent...` until the gate is reached.

The attachment is output-only; Phase 1 does not provide shell stdin. Shell completion, Ctrl+B, Esc, stop, mode switch, or shutdown releases the single-owner `IDisposable` pause lease in a `finally` path. Esc while still pausing cancels the pending attachment. The main agent then resumes.

UI attachment remains controller-local and does not leak into model-facing `task_list`/`task_get`. Do not overload `TaskExecutionMode.Foreground`, which continues to mean that an agent tool call awaits the task. Preserve `TryDetach` for original foreground-shell promotion and expose an authorized awaitable task-completion API.

## Components

- `TaskBrowserState`: pure list/detail/steer state, output source, scroll, auto-follow, and transient status.
- `TaskListProjector`: active hierarchy plus recent history.
- `TaskBrowserController`: TaskManager subscription/resync, selection stability, task actions, pause lease, and cleanup.
- `TaskBrowserOverlay`: Terminal.Gui layout and key routing only.
- `TaskLogTailReader`: bounded asynchronous UTF-8 tail reads that never block the UI thread.
- `AgentExecutionGate`: reference-counted pause gate coordinated with main-turn start/end under one lifecycle lock so idle-check, pause request, and turn start cannot race.

## Opening and Modes

Terminal.Gui intercepts exact `/tasks` submissions in the shell submit handler before `TuiController`'s `dispatchInFlight`/startup rejection guard, so the browser opens during active work.

Plain and Spectre modes register a normal `/tasks` command that prints a single textual snapshot. It does not offer interactive actions.

Both paths use a provider for the live `AgentRunner`/`CodaSession` TaskManager. They never create a throwaway session. Before the first turn, the provider is null and the view prints an empty task list.

## Serve Parity

Add model-facing tools:

- `task_background`
- `task_wait`
- `task_remove`

They join the existing task tools and are registered identically in interactive and serve pipelines. Serve has every non-UI task capability; only the overlay and keyboard shortcuts are TUI-specific.

`task_wait` blocks the calling agent until an authorized task becomes terminal, honors turn cancellation, and accepts an optional timeout (default 10 minutes). Timeout returns `still running` rather than stopping the task. Authorization is checked before waiting so timing cannot reveal another subtree's task.

`task_background` detaches an authorized originally-foreground shell. `task_remove` dismisses an authorized terminal task while preserving its log.

## Errors and Lifecycle

- Unknown and unauthorized tasks are indistinguishable in model-facing tools.
- A pruned/disappearing selected task returns the overlay to the list with a warning.
- Log read failures keep recent output available and show a warning.
- Log tails open with read/write/delete sharing, tolerate concurrent truncate/rewrite, and re-read from EOF after transient short reads.
- Task descriptions and output are control/ANSI-sanitized; Spectre fallback also escapes markup.
- Subscription overflow triggers an authoritative snapshot resync.
- Output-change refresh is coalesced; only the selected task's output is re-read, using non-consuming `TryPeek`.
- Overlay close, mode switch, and shutdown dispose subscriptions, cancel log reads, and release pause leases.
- Composer availability combines startup, prompt, and task-attachment state; ordinary snapshots cannot re-enable it while an attachment pause is active.
- UI actions never hold TaskManager locks or block output producers.

## Testing

- hierarchy projection, active/recent grouping, and stable selection;
- immediate `/tasks` while dispatch is active;
- list/detail navigation and context-sensitive actions;
- subscription overflow/resync;
- recent/log toggle and UTF-8 log-tail boundaries;
- auto-follow and new-output indication;
- steering FIFO and multiline input;
- double-`x` timing;
- attach pause/resume, completion, detach, mode-switch, and shutdown races;
- idle-agent attachment and active-turn-end-before-boundary races;
- Ctrl+B foreground-shell selection;
- dismiss preserves log;
- control/ANSI sanitization and log trim/read races;
- plain/Spectre textual fallback;
- interactive/serve tool-set parity and all new tool authorization contracts.
