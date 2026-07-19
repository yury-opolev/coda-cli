# Exit Session Summary Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Print a resumable session card after every clean interactive exit and add Shift+Enter as a newline shortcut.

**Architecture:** Project mutable runtime state into an immutable `SessionExitSnapshot`, then render it only after the terminal host and actor have drained. Reuse existing pricing, context cache, branding, and `InsertNewline` paths.

**Tech Stack:** .NET 10, C# 14, Spectre.Console 0.55.2, Terminal.Gui 2.4.17, xUnit

---

### Task 1: Exit Snapshot and Renderer

**Files:**
- Create: `src/Coda.Tui/Rendering/SessionExitSnapshot.cs`
- Create: `src/Coda.Tui/Rendering/ExitSummaryRenderer.cs`
- Test: `tests/Coda.Tui.Tests/ExitSummaryRendererTests.cs`

- [ ] Write RED tests for duration, provider/model/effort, message/token totals, pricing, cached exact/estimated context, missing context, no-session text, logo rows, the directory step, and both valid resume commands (including a round-trip through `SessionCli.ParseStartupIntent` and no unsupported `--cwd`).
- [ ] Run `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter FullyQualifiedName~ExitSummaryRendererTests`; expect compile failures.
- [ ] Implement immutable snapshot projection from `SessionState`, `ContextSnapshotCache.Current`, start/end timestamps, `ModelCatalog`, and `Pricing.EstimateUsd`.
- [ ] Implement a pure renderer using `Branding.BannerLines`, accent styling, escaped values, compact statistics, and a conditional directory step plus valid standalone commands (the interactive launcher never consumes `--cwd`, so emit a `cd` step instead of a `--cwd` flag):

```text
Resume from this directory:
cd "<cwd>"
coda --resume "<id>"
coda --continue
```

- [ ] Run focused tests GREEN and commit `feat(tui): render exit session summary`.

### Task 2: Centralize Clean-Exit Rendering

**Files:**
- Modify: `src/Coda.Tui/InteractiveProgram.cs`
- Modify: `src/Coda.Tui/Commands/ExitCommand.cs`
- Test: `tests/Coda.Tui.Tests/InteractiveProgramTests.cs`
- Test: `tests/Coda.Tui.Tests/CommandDispatchTests.cs`

- [ ] Write RED lifecycle tests proving the card renders after a clean host return, once only, for command/chord/EOF seams; it does not render during mode switches or failed startup; output failure does not change exit code.
- [ ] Add injectable `TimeProvider` and renderer seam to `DefaultInteractiveSessionRunner`.
- [ ] Capture start time before startup; after `TuiHost.RunAsync`, dispatch drain, and UI flush, build/render the snapshot to the real restored console.
- [ ] Restrict rendering to clean final exits; remove `ExitCommand`'s `Goodbye.` output.
- [ ] Run lifecycle and full TUI tests GREEN; commit `feat(tui): show summary after clean exit`.

### Task 3: Shift+Enter Newline

**Files:**
- Modify: `src/Coda.Tui/Ui/Input/UiActionMap.cs`
- Modify: `src/Coda.Tui/ImmediateCli.cs`
- Modify: `src/Coda.Tui/Ui/Input/ComposerView.cs`
- Modify: `README.md`
- Test: `tests/Coda.Tui.Tests/ComposerControllerTests.cs`
- Test: `tests/Coda.Tui.Tests/ComposerViewTests.cs`
- Test: `tests/Coda.Tui.Tests/ImmediateCliTests.cs`

- [ ] Write RED tests that `Key.Enter.WithShift` maps to `InsertNewline` and a real composer inserts without submitting.
- [ ] Extend the existing modified-Enter branch:

```csharp
if (key == Key.Enter.WithShift || key == Key.Enter.WithCtrl || key == Key.J.WithCtrl)
{
    return UiAction.InsertNewline;
}
```

- [ ] Document Shift+Enter, Ctrl+Enter, and Ctrl+J, including the terminal-support caveat.
- [ ] Run focused and full TUI tests GREEN; commit `feat(tui): insert newline with Shift+Enter`.

### Task 4: Release Coda

**Files:**
- Modify: `version.json`

- [ ] Bump build from 73 to 74.
- [ ] Run all TUI, Engine, and Auth tests plus `dotnet build LlmAuth.slnx --no-restore` and `git diff --check`.
- [ ] Commit `chore: bump version to 0.1.74`.
- [ ] Request whole-branch review, merge through a PR, sync `main`, run `build.ps1 -NoBump -Test`, publish the tool, and install version 0.1.74 locally.
