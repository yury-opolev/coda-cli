# In-Process Task Manager Design

## Scope

Create one in-process manager for Coda's subagent and shell executions. All work belongs to the current Coda process and stops when it exits.

This project does not add durable workers, daemons, cross-restart execution recovery, `/tasks` TUI, or serve task APIs. Those consumers will use the core snapshots and subscriptions in later projects.

## Task Model

`TaskManager` owns every task and assigns opaque IDs such as `task-0001`.

Each task records:

- ID, optional parent ID, and manager-derived depth;
- type: subagent or shell;
- short description;
- status: running, completed, failed, or stopped;
- start/end timestamps;
- monotonically increasing version;
- bounded recent output;
- persistent log path;
- final result or error.

The manager provides start, list, get, incremental output, peek, stop, remove, detach-shell, send-to-subagent, and subscribe operations.

## Output and Logs

- Each task keeps a 1 MiB drop-oldest in-memory output ring.
- `task_output` preserves its existing incremental main-agent cursor. If eviction overtakes the cursor, the result reports that earlier output was truncated.
- `task_peek` is non-consuming and returns recent assistant text, shell output, and tool activity.
- Complete output is appended to `~/.coda/task-logs/<session-id>/<task-id>.log`.
- Logs use UTF-8, owner-only permissions where supported, and known-secret redaction.
- Logs rotate/truncate at 50 MiB per task.
- Startup cleanup removes logs older than seven days and enforces a 512 MiB global cap.
- Task execution/history remains process-local even though diagnostic logs survive exit.

## Events

Subscriptions receive:

1. a complete immutable snapshot;
2. bounded change notifications containing task ID, version, and change kind.

Each subscriber has a bounded drop-oldest queue. A version gap tells the consumer to resynchronize from manager snapshots. Slow TUI or serve consumers never block task execution or output capture.

## Subagents

- Foreground `task` registers with the manager and waits for completion.
- Background `task_start` registers the same execution type and returns the task ID immediately.
- Each subagent receives a task-specific `SteeringInbox`.
- `task_send` queues a message for the next safe agent-loop boundary.
- `task_stop` cancels the current operation while retaining task status, recent output, and logs.
- Subagents are not user-promoted between foreground/background modes; foreground means only that the parent agent awaits the result.

## Shells

- Every `run_command` execution registers before the child process starts.
- Foreground commands await completion.
- `run_in_background` returns the task ID immediately.
- A foreground shell exposes a detach signal. Detaching releases the waiting tool call with the running task ID while the same child process continues.
- Reopening a detached shell later is a live detail/output view, not restoration of the original tool await.
- Phase 1 does not support writable shell stdin.
- The manager owns shell `Process` handles and terminates complete process trees on stop or shutdown.

## Parent Graph and Nesting

`ToolContext` carries the current task ID and depth.

- Main agent: depth 0.
- Child subagent: depth 1 and may create children.
- Grandchild: depth 2 and receives no task-creation tools.
- `TaskManager` derives parent/depth from trusted execution context and rejects creation beyond depth 2 even if a tool is accidentally exposed.

## Tool Compatibility

Existing tools become adapters over `TaskManager`:

- `task`
- `task_start`
- `task_output`
- `task_stop`

Add:

- `task_list`
- `task_get`
- `task_peek`
- `task_send`

Teams and its planning-board tools have been removed, so these names are unambiguous.

## Lifecycle and Errors

- Unknown IDs return explicit not-found results.
- Steering a shell or terminal task is rejected.
- Steering or stopping an already-terminal task returns an explicit invalid-state result.
- Removing a running task is rejected.
- Shutdown stops accepting starts, cancels subagents, kills shell process trees, waits within the existing bounded shutdown budget, and marks non-responsive tasks failed/stopped.
- Logs remain available after shutdown.

## Testing

- Shared foreground/background subagent execution.
- Shell foreground/background execution and live detachment.
- Shell output, completion, cancellation, and process-tree cleanup.
- Concurrent starts, snapshots, subscriptions, stop, and remove.
- Output ring eviction, cursor truncation, and log retention/capping.
- Secret redaction and file permissions.
- Steering delivery at the next agent boundary.
- Parent-child graph and depth-2 enforcement.
- Compatibility tests for existing task tools.
- Shutdown leaves no running task or child process.

## Future Extension

If durable detached agents are later needed, another Coda process can run a serve/session endpoint and register as an external task. Communication, steering, and results would use the serve protocol. That work is outside this project.
