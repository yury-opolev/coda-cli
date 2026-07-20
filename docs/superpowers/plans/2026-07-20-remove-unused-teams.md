# Remove Unused Teams Subsystem Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the unused Teams feature and eliminate its collisions with execution `task_*` tools.

**Architecture:** Delete the Teams domain, tools, SDK/TUI adapters, and wiring in one coherent change. Preserve subagents, background tasks, todos, schedules, hooks, and user data on disk.

**Tech Stack:** .NET 10, C# 14, xUnit

---

### Task 1: Remove Teams Domain and Wiring

**Files:**
- Delete: `src/Coda.Agent/Teams/**`
- Delete: `src/Coda.Agent/Tools/TeamCreateTool.cs`
- Delete: `src/Coda.Agent/Tools/TeamDeleteTool.cs`
- Delete: `src/Coda.Agent/Tools/SpawnTeammateTool.cs`
- Delete: `src/Coda.Agent/Tools/SendMessageTool.cs`
- Delete: `src/Coda.Agent/Tools/TaskCreateTool.cs`
- Delete: `src/Coda.Agent/Tools/TaskListTool.cs`
- Delete: `src/Coda.Agent/Tools/TaskGetTool.cs`
- Delete: `src/Coda.Agent/Tools/TaskUpdateTool.cs`
- Delete: `src/Coda.Agent/Tools/TaskStopTool.cs`
- Delete: `src/Coda.Sdk/InProcessTeammateAgent.cs`
- Delete: `src/Coda.Tui/Commands/TeamCommand.cs`
- Modify: `src/Coda.Agent/AgentLoop.cs`
- Modify: `src/Coda.Agent/ITool.cs`
- Modify: `src/Coda.Sdk/AgentLoopSpec.cs`
- Modify: `src/Coda.Sdk/CodaSession.cs`
- Modify: `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs`
- Modify: `src/Coda.Tui/Repl/SlashCommandCatalog.cs`
- Delete: Teams-focused tests under `tests/Engine.Tests/Teams/**` and `tests/Coda.Tui.Tests/TeamCommandTests.cs`

- [ ] Add a RED registry test proving duplicate tool names are rejected or absent, and execution `task_stop` resolves to `BackgroundTaskStopTool`.
- [ ] Delete Teams files and remove all constructor fields, context properties, tool registration, runtime snapshot data, session initialization/disposal, and TUI command registration.
- [ ] Preserve `SubagentHost`, `TaskTool`, `BackgroundTask*Tool`, `TodoStore`, `TodoWriteTool`, schedules, hooks, and permissions.
- [ ] Run focused pipeline, permission, background-task, command-catalog, and SDK characterization tests.
- [ ] Search source/tests for remaining Teams symbols and removed tool names; only historical design docs may remain.
- [ ] Commit `refactor(agent): remove unused Teams subsystem`.

### Task 2: Documentation and Release

**Files:**
- Modify: `docs/API.md`
- Modify: `docs/architecture-overview.md`
- Modify: `version.json`

- [ ] Remove active API/architecture claims about Teams without deleting historical design records.
- [ ] Bump build from 76 to 77.
- [ ] Run all TUI, Engine, and Auth tests, warning-free solution build, and `git diff --check`.
- [ ] Commit `chore: release Coda 0.1.77`.
- [ ] Request whole-branch review, merge through a PR, sync `main`, build/package, and install Coda 0.1.77 locally.
