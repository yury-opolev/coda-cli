# Remove Unused Teams Subsystem Design

## Goal

Remove Coda's unused multi-agent Teams feature so `task_*` names can represent running subagent and shell executions without collisions.

## Removal Scope

- Delete `Coda.Agent/Teams/**`.
- Delete team lifecycle/messaging tools and the Teams planning-board tools.
- Delete `InProcessTeammateAgent` and the TUI `/team` command.
- Remove team collaborators from `ToolContext`, `AgentLoop`, `AgentLoopSpec`, `CodaSession`, `TurnPipelineBuilder`, and runtime snapshots.
- Remove Teams tests and documentation.

## Preserved Features

- `SubagentHost` and foreground `task`.
- Background `task_start`, `task_output`, and execution `task_stop`.
- `TodoStore` and `todo_write`.
- Schedules, hooks, permissions, session state, and MCP tools.

Existing `.coda/teams` files are not deleted or migrated; they simply become unused.

## Validation

- Add/retain tests proving execution task tools have unique names and `task_stop` is not shadowed.
- Search source, tests, and docs for remaining Teams symbols/tool names.
- Run all TUI, Engine, and Auth tests plus a warning-free solution build.
- Release through a pull request and update the locally installed Coda tool.
