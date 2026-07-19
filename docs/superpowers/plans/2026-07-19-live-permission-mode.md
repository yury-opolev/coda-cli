# Live Permission Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make permission-mode changes affect the next tool decision of already-running main and subagent loops.

**Architecture:** Replace copied permission enums with one atomic session-scoped `PermissionModeState`. Every `ModePermissionPrompt` reads that state per request; TUI session options and subagent hosts share the same reference.

**Tech Stack:** .NET 10, C# 14, xUnit

---

### Task 1: Shared Live Permission State

**Files:**
- Create: `src/Coda.Agent/PermissionModeState.cs`
- Modify: `src/Coda.Agent/ModePermissionPrompt.cs`
- Modify: `src/Coda.Sdk/SessionOptions.cs`
- Modify: `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs`
- Modify: `src/Coda.Tui/Repl/SessionState.cs`
- Modify: `src/Coda.Tui/Agent/AgentRunner.cs`
- Test: `tests/Engine.Tests/PermissionModeTests.cs`
- Test: `tests/Coda.Tui.Tests/AgentRunnerTests.cs`

- [ ] Write RED tests: Default asks, mutate shared state to Bypass and next write allows without inner prompt, switch back and next write asks; two prompts sharing the state observe the same update; TUI `BuildOptions` passes the exact session state reference.
- [ ] Run focused tests and verify expected compile/behavior failures.
- [ ] Implement `PermissionModeState` with an `int` backing field and `Volatile.Read/Write`.
- [ ] Add a `PermissionModeState` constructor overload to `ModePermissionPrompt`; retain the enum overload by wrapping a fixed state.
- [ ] Add optional `PermissionModeState?` to `SessionOptions`; `TurnPipelineBuilder` uses it or creates a fixed state from `PermissionMode`.
- [ ] Make `SessionState.PermissionMode` delegate to its stable `PermissionModes` instance.
- [ ] Pass `context.Session.PermissionModes` from `AgentRunner.BuildOptions`; subagents already share the built permission prompt.
- [ ] Run focused and full Engine/TUI tests; commit `fix(agent): apply permission mode changes live`.

### Task 2: Release

**Files:**
- Modify: `version.json`

- [ ] Bump build from 75 to 76.
- [ ] Run all TUI, Engine, and Auth tests plus warning-free solution build and `git diff --check`.
- [ ] Commit `chore: bump version to 0.1.76`.
- [ ] Request whole-branch review, merge through a PR, sync `main`, build/package, and install Coda 0.1.76 locally.
