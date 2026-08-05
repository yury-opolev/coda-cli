# Subagent Orchestration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the main agent pick a model for each subagent (same provider), raise default fan-out to 20, restrict the main agent's toolset by name from settings, and push background-task completions into the owning agent's context instead of requiring polling.

**Spec:** [`docs/proposals/2026-08-04-subagent-orchestration.md`](../../proposals/2026-08-04-subagent-orchestration.md) — decisions D1–D5 are closed there; do not re-litigate them.

**Architecture:** Model selection resolves to a single string applied to the child loop's `AgentOptions.Model`, which already flows to `ChatRequest.Model` — no new client plumbing. The main-agent tool restriction is applied by filtering the *parent* registry at composition time, never via `TurnShape`, so subagents are unaffected by construction. Completion push is a fourth `AgentLoop` injection seam fed by a per-owner outbox on `TaskManager`, filled at every terminal transition (Completed, Failed, Stopped).

**Tech Stack:** .NET 10, C# 14, xUnit. Test projects: `tests/Engine.Tests` (agent/SDK), `tests/Coda.Tui.Tests` (TUI/plugins).

**Baseline:** `main` @ `44cc369`, version 0.1.100.

---

## Invariants that must not regress

These are load-bearing and each has a dedicated regression test in this plan:

1. A restricted main agent still yields subagents with a **full** toolset (Task 7).
2. Background completions are delivered for **Failed and Stopped**, not only Completed (Task 8).
3. A **project-scoped** plugin or project settings file can never choose a subagent model (Tasks 2, 3).
4. Slot accounting stays balanced — no leaked or double-released slots on any path (Tasks 4, 5).

---

## File Structure

- Modify `src/Coda.Agent/Settings/SubagentSettings.cs` — `Model`, `ModelByType`, `MaxConcurrent` default 20.
- Modify `src/Coda.Agent/Settings/CodaSettings.cs` — `SubagentOverrides` model fields; new `AgentToolSettings`.
- Modify `src/Coda.Agent/Settings/SettingsLoader.cs` — parse `subagents.model`/`modelByType` (user file only) and `agent.tools.allow`/`deny`.
- Modify `src/Coda.Agent/Subagents/SubagentDefinition.cs` — add `Model`.
- Modify `src/Coda.Agent/Subagents/SubagentRequest.cs` — add `Model`.
- Modify `src/Coda.Agent/SubagentHost.cs` — resolve the model and apply it to child `AgentOptions`.
- Modify `src/Coda.Agent/Tools/TaskTool.cs` — `model` input, pass through.
- Modify `src/Coda.Agent/Tools/BackgroundTaskStartTool.cs` — `model` input, pass through.
- Modify `src/Coda.Agent/Tasks/TaskManager.Subagents.cs` — thread the model into the request; record it.
- Modify `src/Coda.Agent/Tasks/ManagedTask.cs` — record the resolved model.
- Modify `src/Coda.Agent/Tasks/TaskManager.cs` — completion outbox; enqueue on all terminal transitions; orphan roll-up.
- Modify `src/Coda.Agent/AgentLoop.cs` — completion-push injection seam.
- Modify `src/Coda.Agent/Tools/TaskWaitTool.cs` — consume the outbox entry it reports.
- Create `src/Coda.Agent/Tools/ToolNameFilter.cs` — shared allow/deny name filtering.
- Modify `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs` — filter `BuildParentTools` only; inert-agent guard.
- Modify `src/Coda.Sdk/SessionOptions.cs` — carry the resolved main-agent tool filter.
- Modify `src/Coda.Sdk/Serve/ServeMethods.cs` — `event/taskCompleted`.
- Create `src/Coda.Sdk/Serve/Messages/TaskCompletedEvent.cs` — wire DTO.
- Modify `src/Coda.Tui/Plugins/PluginAgentLoader.cs` — read `model` for user-scoped plugins only; update the guarantee comment.
- Modify `tests/Engine.Tests/Settings/SubagentSettingsTests.cs`, `tests/Engine.Tests/Subagents/SubagentLimitsWiringTests.cs`, `tests/Engine.Tests/Subagents/SubagentConcurrencyTests.cs`, `tests/Engine.Tests/SubagentRestrictionTests.cs`, `tests/Engine.Tests/Tasks/TaskManagerTests.cs`.
- Create `tests/Engine.Tests/Subagents/SubagentModelSelectionTests.cs`.
- Create `tests/Engine.Tests/Settings/AgentToolSettingsTests.cs`.
- Create `tests/Engine.Tests/Tasks/TaskCompletionOutboxTests.cs`.
- Create `tests/Engine.Tests/MainAgentToolRestrictionTests.cs`.
- Modify `tests/Coda.Tui.Tests/` plugin agent-loader tests — user vs project `model` scope.
- Modify `README.md` and `docs/serve-protocol.md`.
- Modify `version.json` — bump 0.1.100 → 0.1.101.

---

## Phase 1 — Per-subagent model

### Task 1: Subagent model settings

**Files:**
- Modify: `src/Coda.Agent/Settings/SubagentSettings.cs`
- Test: `tests/Engine.Tests/Settings/SubagentSettingsTests.cs`

- [ ] **Step 1: Write failing tests** — `Model` defaults to null; `ModelByType` defaults to empty; both round-trip through `with`; `ModelByType` lookup is case-insensitive on the subagent type; blank/whitespace `Model` normalises to null.
- [ ] **Step 2: Implement** — add `public string? Model { get; init; }` and `public IReadOnlyDictionary<string, string> ModelByType { get; init; } = ...OrdinalIgnoreCase empty`. Normalise blank to null in the initialiser, mirroring the existing clamping style.
- [ ] **Step 3: Verify** — `dotnet test tests\Engine.Tests\Engine.Tests.csproj --no-restore --filter "FullyQualifiedName~SubagentSettingsTests"`

### Task 2: Settings loading — user file only

**Files:**
- Modify: `src/Coda.Agent/Settings/CodaSettings.cs`, `src/Coda.Agent/Settings/SettingsLoader.cs`
- Test: `tests/Engine.Tests/Settings/SubagentSettingsTests.cs`

- [ ] **Step 1: Write failing tests** — a **user** settings file setting `subagents.model` / `subagents.modelByType` is honoured; a **project** file setting either is **ignored**, and a warning is logged naming the file; absent leaves both null/empty; `maxDepth`/`maxConcurrent` keep their existing project-wins merge.
- [ ] **Step 2: Implement** — extend `SubagentOverrides` with the two fields and, in `Merge`, take them from `user` only — exactly as `AllowSystemPromptReplacement` already does (`CodaSettings.cs:122-130`). Add the JSON properties to the loader DTO (`SettingsLoader.cs:624-656`).
- [ ] **Step 3: Verify** — same filter as Task 1.

**Note:** this is invariant 3. The security rationale is `SubagentSettings.cs:52-59` — a project settings file is attacker-controlled after cloning a hostile repo, and model choice is a cost lever.

### Task 3: Plugin-declared model, user-scoped plugins only

**Files:**
- Modify: `src/Coda.Agent/Subagents/SubagentDefinition.cs`, `src/Coda.Tui/Plugins/PluginAgentLoader.cs`
- Test: `tests/Coda.Tui.Tests/` (the existing plugin agent-loader test file), `tests/Engine.Tests/SubagentPluginTests.cs`

- [ ] **Step 1: Write failing tests** — a **user-scoped** plugin agent whose frontmatter has `model: X` produces `SubagentDefinition.Model == "X"`; the **same file in a project-scoped plugin** produces `Model == null` and logs a warning naming plugin and file; a plugin with no `model` key produces null.
- [ ] **Step 2: Implement** — add `string? Model = null` as a trailing optional positional on the `SubagentDefinition` record so existing construction sites keep compiling. In `PluginAgentLoader`, read `model` from frontmatter only when the plugin is user-scoped, using the existing public seam `PluginComponentComposer.IsProjectPlugin(plugin, workingDirectory)` (`PluginComponentComposer.cs:382-383`).
- [ ] **Step 3: Update the stale guarantee** — the comment at `PluginAgentLoader.cs:65-70` currently promises that only the four `SubagentDefinition` fields are ever read. Rewrite it to state the new invariant: `model` is read for user-scoped plugins only, everything else still lands in `UnknownFields` unread.
- [ ] **Step 4: Verify** — `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --no-restore --filter "FullyQualifiedName~PluginAgent"` and the `SubagentPluginTests` filter in `Engine.Tests`.

### Task 4: Resolution and application in `SubagentHost`

**Files:**
- Modify: `src/Coda.Agent/Subagents/SubagentRequest.cs`, `src/Coda.Agent/SubagentHost.cs`
- Test: Create `tests/Engine.Tests/Subagents/SubagentModelSelectionTests.cs`

- [ ] **Step 1: Write failing tests** — one test per precedence level, asserting the model that reaches the child loop's `ChatRequest`:
  1. `request.Model` wins over everything
  2. `settings.Subagents.ModelByType[type]` wins over `Model` and the definition
  3. `settings.Subagents.Model` wins over the definition
  4. `definition.Model` applies when the operator said nothing
  5. falls back to `baseOptions.Model`

  Plus: whitespace-only at any level is treated as absent; control characters are stripped before the value is used; the session model is untouched for the *parent* loop.
- [ ] **Step 2: Implement** — add `string? Model { get; init; }` to `SubagentRequest`. In `RunSubagentAsync`, resolve once (private static helper, unit-testable) and apply at the existing options construction (`SubagentHost.cs:264-269`):
  ```csharp
  var options = this.baseOptions with
  {
      SystemPrompt = systemPrompt,
      Model = resolvedModel,
      MaxIterations = Math.Min(this.baseOptions.MaxIterations, 500),
  };
  ```
  Read settings from `this.tasks.SubagentSettings` — the same accessor the host already uses for depth and prompt policy (`SubagentHost.cs:60`), so limits and models can never come from two configurations.
- [ ] **Step 3: Verify** — `dotnet test tests\Engine.Tests\Engine.Tests.csproj --no-restore --filter "FullyQualifiedName~SubagentModelSelectionTests"`

**Note:** do **not** add validation. Unknown ids surface as the provider's own error (spec §3.5). Ensure the provider message is propagated verbatim so an effort/model incompatibility is at least diagnosable.

### Task 5: `model` argument on the launch tools

**Files:**
- Modify: `src/Coda.Agent/Tools/TaskTool.cs`, `src/Coda.Agent/Tools/BackgroundTaskStartTool.cs`, `src/Coda.Agent/Tasks/TaskManager.Subagents.cs`, `src/Coda.Agent/Tasks/ManagedTask.cs`
- Test: `tests/Engine.Tests/Tasks/NewTaskToolsTests.cs`, `tests/Engine.Tests/Subagents/SubagentModelSelectionTests.cs`

- [ ] **Step 1: Write failing tests** — `task` and `task_start` accept `model` and it reaches `SubagentRequest.Model`; omitting it yields null; the resolved model is recorded on the `ManagedTask` and visible via `task_get`/`task_list`; **the subagent slot is released exactly once** on the success path, on a thrown launch, and on `SubagentStartBlockedException`.
- [ ] **Step 2: Implement** — add `model` to both `InputSchemaJson` blocks with a description stating it must be a model of the session's provider; read via `ToolInput.GetString`; thread through `RunSubagentForegroundAsync` / `StartSubagentBackground` into `SubagentRequest`. Record the resolved model on `ManagedTask`.
- [ ] **Step 3: Verify** — `dotnet test tests\Engine.Tests\Engine.Tests.csproj --no-restore --filter "FullyQualifiedName~NewTaskToolsTests|FullyQualifiedName~SubagentModelSelectionTests"`

---

## Phase 2 — Fan-out

### Task 6: Default fan-out 20

**Files:**
- Modify: `src/Coda.Agent/Settings/SubagentSettings.cs`
- Test: `tests/Engine.Tests/Settings/SubagentSettingsTests.cs`, `tests/Engine.Tests/Subagents/SubagentConcurrencyTests.cs`, `tests/Engine.Tests/Subagents/SubagentLimitsWiringTests.cs`

- [ ] **Step 1: Write failing tests** — default `MaxConcurrent` is 20; clamp `[1, 64]` still holds at both ends; the 21st concurrent launch is refused with the existing message naming `subagents.maxConcurrent`; **20 sequential `task_start` calls yield 20 concurrently running tasks** (this is the user-facing goal — assert it explicitly).
- [ ] **Step 2: Implement** — change the `maxConcurrent` field initialiser from 8 to 20. Update the XML doc that calls the defaults "the historic constants".
- [ ] **Step 3: Update any test asserting 8** — search the suites for hardcoded 8s before assuming a green run.
- [ ] **Step 4: Verify** — `dotnet test tests\Engine.Tests\Engine.Tests.csproj --no-restore --filter "FullyQualifiedName~SubagentSettingsTests|FullyQualifiedName~SubagentConcurrencyTests|FullyQualifiedName~SubagentLimitsWiringTests"`

**Note:** the slot budget is session-wide, not per depth, so a 20-wide leader starves deeper levels. Document it in the README as a *fleet* budget (spec §4.4); a per-depth reservation is an explicit follow-up, not part of this work.

---

## Phase 3 — Main-agent tool restriction

### Task 7: `agent.tools.allow` / `agent.tools.deny`

**Files:**
- Create: `src/Coda.Agent/Tools/ToolNameFilter.cs`
- Modify: `src/Coda.Agent/Settings/CodaSettings.cs`, `src/Coda.Agent/Settings/SettingsLoader.cs`, `src/Coda.Sdk/SessionOptions.cs`, `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs`
- Test: Create `tests/Engine.Tests/Settings/AgentToolSettingsTests.cs`, `tests/Engine.Tests/MainAgentToolRestrictionTests.cs`

- [ ] **Step 1: Write failing settings tests** — `agent.tools.allow`/`deny` parse; absent = no restriction; **empty `allow` array is honoured literally** (not treated as absent); merge across user and project files intersects `allow` and unions `deny`; a project file cannot widen what the user file allowed; unknown tool names are inert (no error).
- [ ] **Step 2: Write failing filter tests** — `ToolNameFilter` applies `allow` then `deny`; deny wins when a name is in both; matching is by `ITool.Name`, case-insensitive; MCP/plugin tools from `ExtraTools` are filtered by the same rule.
- [ ] **Step 3: Write the non-propagation regression test** — invariant 1. Compose a session whose `agent.tools.allow` is `["task", "task_start"]`, then assert the registry handed to `SubagentHost` still contains the full toolset (`read_file`, `run_command`, …). This is the test that would have caught implementing the feature as a `TurnShape.AllowedTools`.
- [ ] **Step 4: Write the inert-agent guard test** — a resolved main-agent toolset containing neither `task` nor `task_start` is refused at load with an error naming the offending settings file.
- [ ] **Step 5: Implement** — new `AgentToolSettings { IReadOnlyList<string>? Allow; IReadOnlyList<string> Deny; }` on `CodaSettings` under an `agent` block; loader parsing plus the tightening merge; carry the resolved filter on `SessionOptions`; apply it in **`TurnPipelineBuilder.BuildParentTools`** (`:567`) **only**. Leave `BuildSubagentHost` (`:501`), `BuildScheduledTools` (`:382`) and the scheduled-root subagent host (`:335`) untouched.
- [ ] **Step 6: Verify** — `dotnet test tests\Engine.Tests\Engine.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentToolSettingsTests|FullyQualifiedName~MainAgentToolRestrictionTests|FullyQualifiedName~SubagentRestrictionTests"`

**Do not** express this as a `TurnShape` — `TurnShapeResolver.ToToolRestrictionShape()` (`:85-101`) propagates the parent's allowlist to every child and would leave subagents with zero tools. Composition-time filtering is what makes non-propagation structural.

### Task 8: Document what still propagates

**Files:**
- Test: `tests/Engine.Tests/SubagentRestrictionTests.cs`
- Modify: `README.md`

- [ ] **Step 1: Write characterisation tests** — a skill that sets `DisallowedTools` **does** still narrow subagents (`SkillTurnShapeComposer.cs:53` → `DeniedOnlyInput` → `ToToolRestrictionShape()`), and a hook setting `allowedTools` **does** still propagate. These lock existing behaviour so a future change to it is deliberate rather than accidental.
- [ ] **Step 2: Document** — README states plainly that `agent.tools` restricts the **main agent only**; subagents, scheduled roots (`:382`) and hook-spawned agents (`:531`) keep full tools; and that this is a workflow control, **not a security boundary**.
- [ ] **Step 3: Verify** — `dotnet test tests\Engine.Tests\Engine.Tests.csproj --no-restore --filter "FullyQualifiedName~SubagentRestrictionTests"`

---

## Phase 4 — Completion push

### Task 9: Completion outbox on `TaskManager`

**Files:**
- Modify: `src/Coda.Agent/Tasks/TaskManager.cs`
- Test: Create `tests/Engine.Tests/Tasks/TaskCompletionOutboxTests.cs`

- [ ] **Step 1: Write failing tests** — invariant 2 first:
  - `Complete`, **`Fail` and `Stop`** each enqueue for a background task
  - a foreground task enqueues nothing (its report is already the tool result)
  - entries are keyed by owner (`ParentId`, main agent when null) and an agent never sees another subtree's completions
  - draining is exactly-once
  - **orphan roll-up:** owner A terminates while its background child B runs; when B completes, the entry lands on A's nearest *live* strict ancestor, not on dead A
  - the outbox is bounded and cannot grow without limit against a dead owner
- [ ] **Step 2: Implement** — enqueue at the **terminal transition**, covering all three methods (`TaskManager.cs:554-586`). Do **not** co-locate with `NotificationCallback`: it fires only from `Complete`, so wiring there would make failed and stopped workers invisible. Reuse the ancestor walk from `IsAuthorizedCaller` (`TaskManager.Subagents.cs:158-176`) for the roll-up.
- [ ] **Step 3: Verify** — `dotnet test tests\Engine.Tests\Engine.Tests.csproj --no-restore --filter "FullyQualifiedName~TaskCompletionOutboxTests|FullyQualifiedName~TaskManagerTests"`

### Task 10: `AgentLoop` injection seam

**Files:**
- Modify: `src/Coda.Agent/AgentLoop.cs`
- Test: `tests/Engine.Tests/Tasks/TaskCompletionOutboxTests.cs`

- [ ] **Step 1: Write failing tests** — completions for the current agent are injected as a synthetic user message before the next model call; **the steering seam drains first**, completions second; reports are truncated at the cap with an explicit truncation notice pointing at `task_output`; an overflow of tasks is summarised as a count rather than injected wholesale; nothing is injected when the outbox is empty; the injected message is a plain user message that does not disturb thinking/`tool_use`/`tool_result` ordering (`AgentLoop.cs:620-645`).
- [ ] **Step 2: Implement** — add the seam immediately **after** the steering drain (`AgentLoop.cs:414-425`), matching the surrounding style. Format:
  ```
  <task-completed id="…" status="completed|failed|stopped" description="…">
  …report or error, truncated…
  (truncated — use task_output for the full log)
  </task-completed>
  ```
- [ ] **Step 3: Verify** — same filter as Task 9.

### Task 11: `task_wait` consumes what it reports

**Files:**
- Modify: `src/Coda.Agent/Tools/TaskWaitTool.cs`
- Test: `tests/Engine.Tests/Tasks/TaskManagerWaitingTests.cs`

- [ ] **Step 1: Write failing tests** — when `task_wait` returns terminal for task X, X's outbox entry for that owner is consumed and the push does **not** also deliver it; a timeout consumes nothing and leaves the task running; a task nobody waited on is still pushed.
- [ ] **Step 2: Implement** — consume on the terminal return path only, and carry the report in the wait result.
- [ ] **Step 3: Verify** — `dotnet test tests\Engine.Tests\Engine.Tests.csproj --no-restore --filter "FullyQualifiedName~TaskManagerWaitingTests|FullyQualifiedName~TaskCompletionOutboxTests"`

### Task 12: Serve parity — `event/taskCompleted`

**Files:**
- Modify: `src/Coda.Sdk/Serve/ServeMethods.cs`, `src/Coda.Sdk/Serve/WireAgentSink.cs`
- Create: `src/Coda.Sdk/Serve/Messages/TaskCompletedEvent.cs`
- Modify: `docs/serve-protocol.md`
- Test: the serve test file covering event shapes in `tests/Engine.Tests`

- [ ] **Step 1: Write failing test** — the event is emitted with `{taskId, status, description, report}` and the report is truncated identically to the TUI path.
- [ ] **Step 2: Implement** — mirror an existing event end to end (`EventSubagentBlocked` is the closest precedent).
- [ ] **Step 3: Document** — add the event to `docs/serve-protocol.md`.
- [ ] **Step 4: Verify** — the serve-event test filter.

**Note:** full task RPC parity (`session/taskList`, `session/taskGet`, …) is out of scope — serve has no task surface today and only this event is added.

---

## Phase 5 — Wrap-up

### Task 13: Documentation and version

**Files:**
- Modify: `README.md`, `version.json`

- [ ] **Step 1: README** — document `subagents.model` / `subagents.modelByType` (user settings file only), the `model` argument on `task`/`task_start`, the new fan-out default of 20 and its fleet-budget caveat, `agent.tools.allow`/`deny` with the main-agent-only scope and the not-a-security-boundary warning, and completion push with its truncation behaviour. Include the delegation-only example config and the §5.6 costs of running without file tools.
- [ ] **Step 2: Version** — bump `version.json` build 100 → 101.

### Task 14: Review

- [ ] **Step 1** — run the full `Engine.Tests` and `Coda.Tui.Tests` suites plus a `-warnaserror` Release build.
- [ ] **Step 2** — exactly one review subagent over the whole branch. Fix critical and important findings before merging; defer minor and low.
- [ ] **Step 3** — commit, push, open a PR.

---

## Verification commands

```powershell
# Focused (preferred during implementation)
dotnet test tests\Engine.Tests\Engine.Tests.csproj --no-restore --filter "FullyQualifiedName~<TestClass>"

# Full suites (final phase only)
dotnet test tests\Engine.Tests\Engine.Tests.csproj --no-restore
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --no-restore

# Release build, warnings as errors
.\build.ps1 -NoBump
```
