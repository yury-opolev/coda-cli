# Dialog, Queue, and Transcript UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the approved dialog/queue/transcript UX: blank-row block separators, a user-global `toolDisplayMode`, reliable Shift+Enter in Windows Terminal, an ID-bearing shared steering queue delivered at the earliest safe boundary (including between sequential tools), visible/recallable queued TUI messages, and jump-to-bottom + scrollbar navigation chrome — without ever removing data from engine/serve/history/audit/telemetry surfaces.

**Architecture:** Five independently-implementable groups (A rendering, B queue engine + serve/task APIs, C viewport UX, D Windows Terminal input) build against stable seams, then group E integrates them into the TUI. Rendering changes live in the shared `TranscriptBlockFormatter`/`TranscriptLayoutIndex`/`VirtualizedTranscriptView` so inline and fullscreen shells inherit them. The steering primitive evolves `Coda.Agent.SteeringInbox` in place (kept name, new ID + seal semantics) so its ~15 call sites migrate mechanically; `AgentLoop` gains safe-boundary delivery + provider-valid skipped tool results; serve/task APIs gain additive fields, one method, and one event.

**Tech Stack:** .NET 10, C# 14, Terminal.Gui 2.4.17, xUnit 2.9.3. Test projects: `tests/Engine.Tests` (namespace `Engine.Tests`) and `tests/Coda.Tui.Tests` (namespace `Coda.Tui.Tests`). No central package management; Terminal.Gui is referenced only in `src/Coda.Tui/Coda.Tui.csproj`.

---

## Workflow (approved)

- Groups A–D are independent and MUST be built in parallel by fast implementation agents (GPT-5.6 Luna / GPT-5.6 Terra). Group E integrates A–D; Group F verifies.
- **No per-task or per-group code review.** After ALL groups are integrated and Group F passes, run exactly **one holistic review with the strongest available model** for a single final verdict.
- TDD throughout: write the failing test, run it RED, implement minimally, run it GREEN, commit. Commit after every task.
- Every commit message ends with the trailer:
  `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`

---

## Spec inconsistencies discovered (resolve as noted; confirm with author if blocked)

1. **"Invalid `toolDisplayMode` values produce a warning through the existing logging path" — there is no logging path in settings loading.** `src/Coda.Agent/Settings/SettingsLoader.cs` is a static class with **no `ILogger`**; today invalid enum-ish values (e.g. telemetry `level`) are silently dropped via `Enum.TryParse` fallback (`SettingsLoader.cs:329-333`). **Resolution in this plan:** `SettingsLoader` reads the *raw* string into `CodaSettings.ToolDisplayMode` (nullable, user-only) and does NOT interpret it; the TUI composition root (`InteractiveProgram`) resolves it via a pure `ToolDisplayModeResolver` and emits the invalid-value warning through the TUI's existing notice/logging surface (`NotificationEvent` / telemetry logger). See Tasks A3, A4, A8.

2. **Jump-hint "new message" count must be block-based, but `TranscriptViewportState.UnseenRows` is row-based and increments on streaming growth.** `TranscriptViewportState.OnRowsAppended` (`TranscriptViewportState.cs:80`) counts wrapped ROWS, and `VirtualizedTranscriptView.ReplaceBlock` feeds streaming deltas into it (`VirtualizedTranscriptView.cs:152`). The spec requires counting newly appended *visible blocks* and NOT incrementing on streaming growth. **Resolution:** add a SEPARATE block-level counter (`UnseenBlocks`) fed only by `Append` of a visible block; leave the existing row-based `UnseenRows` untouched (it is still used by nothing after the header text is replaced, but keeping it avoids churn). See Tasks C1, C2.

3. **`task_recall` "available through the same deferred-tool … paths as the existing task tools," but the existing task tools are NOT deferred.** Only `Coda.Mcp.McpTool` overrides `ShouldDefer => true`; `TaskSendTool` is inline (`grep ShouldDefer`). The testing section §9 explicitly requires "Deferred tool discovery includes the new task recall tool." **Resolution:** implement `TaskRecallTool` with `ShouldDefer => true` (so `DeferredTools.IsDeferred(new TaskRecallTool())` is true), registered in `BuiltInTools.All()` alongside `TaskSendTool`. This satisfies §9 even though its sibling `task_send` stays inline. See Task B8.

4. **Minor — `UiAction.JumpToNewest` already exists** (`src/Coda.Tui/Ui/Input/UiAction.cs:37`) but is used by the task browser, not the composer. Do not redefine it; the shell-global Ctrl+End (Task C4) is wired at the shell key seam, not the composer action map.

---

## File map

### Group A — Rendering foundation
- Modify: `src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs` — owns the trailing blank separator row (row-count math + emission).
- Modify: `src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs` — none in A (see C).
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs` — `TranscriptRow.IsSeparator`; skip separators for expansion/selection.
- Create: `src/Coda.Tui/Ui/Rendering/ToolDisplayMode.cs` — enum + `ToolDisplayModeResolver`.
- Modify: `src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs` — mode-aware `Format`/`AppendTool`.
- Modify: `src/Coda.Tui/Ui/State/OperationalStatusProjector.cs` — tiny-mode generic "Working".
- Modify: `src/Coda.Tui/Ui/Rendering/PlainOutputRenderer.cs` — mode-aware plain fallback.
- Modify: `src/Coda.Agent/Settings/CodaSettings.cs`, `src/Coda.Agent/Settings/SettingsLoader.cs` — user-only raw `ToolDisplayMode` string.
- Modify: `src/Coda.Tui/InteractiveProgram.cs` — resolve mode, thread into shells/plain renderer, warn on invalid.
- Test: `tests/Coda.Tui.Tests/TranscriptLayoutIndexTests.cs`, `TranscriptBlockFormatterTests.cs`, `OperationalStatusProjectorTests.cs`, `tests/Engine.Tests/Settings/SettingsDefaultsTests.cs`, new `tests/Coda.Tui.Tests/ToolDisplayModeResolverTests.cs`.

### Group B — Queue engine and APIs
- Modify: `src/Coda.Agent/SteeringInbox.cs` — evolve to ID-bearing queue with seal; add `SteeringEntry`.
- Modify: `src/Coda.Agent/AgentLoop.cs` — safe-boundary delivery, between-tool skipped results, seal at completion, fire `OnSteeringDelivered`.
- Modify: `src/Coda.Agent/IAgentSink.cs` — `OnSteeringDelivered` default method.
- Modify: `src/Coda.Sdk/RecordingSink.cs`, `src/Coda.Sdk/Serve/WireAgentSink.cs`, `src/Coda.Agent/SubagentHost.cs` (CollectingSink), `src/Coda.Agent/Tasks/TaskManager.Subagents.cs` (TaskOutputSink) — forward the new event.
- Modify: `src/Coda.Sdk/CodaSession.cs` — `Steer` returns id; add `RecallSteering`; reopen queue at turn start.
- Modify: `src/Coda.Sdk/Serve/ServeHost.cs`, `src/Coda.Sdk/Serve/ServeMethods.cs` — `session/steer` messageId; `session/recallSteering`; `event/steeringDelivered`.
- Create: `src/Coda.Sdk/Serve/Messages/RecallSteeringResult.cs`, `RecalledSteeringMessage.cs`, `SteeringDeliveredEvent.cs`.
- Modify: `src/Coda.Sdk/Serve/Messages/SteerResult.cs` — add `MessageId`.
- Modify: `src/Coda.Agent/Tasks/TaskManager.Subagents.cs` — `RecallSteering(id, callerTaskId)`.
- Create: `src/Coda.Agent/Tools/TaskRecallTool.cs`; Modify: `src/Coda.Agent/Tools/BuiltInTools.cs`.
- Test: `tests/Engine.Tests/SteeringInboxTests.cs` (new), `AgentLoopTests.cs`, `Serve/ServeProtocolTests.cs`, `Serve/ServeHostTests.cs`, `RecordingSinkForwardingTests.cs`, `Tasks/NewTaskToolsTests.cs`.

### Group C — Viewport UX
- Modify: `src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs` — `UnseenBlocks` counter.
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs` — block-append signal, reserved scrollbar column, thumb draw + mouse.
- Create: `src/Coda.Tui/Ui/Shells/ScrollbarMetrics.cs` — pure thumb math.
- Create: `src/Coda.Tui/Ui/Shells/JumpToBottomHint.cs` — floating hint view.
- Modify: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs` — build hint, replace header unseen text, wire clicks.
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs` — shell-global Ctrl+End in `TryHandleShellKey`.
- Test: `tests/Coda.Tui.Tests/TranscriptViewportStateTests.cs` (new), `ScrollbarMetricsTests.cs` (new), `FullscreenTuiShellTests.cs`.

### Group D — Windows Terminal input compatibility
- Create: `src/Coda.Tui/Ui/Host/TerminalInputCompatibility.cs` — WT detection + driver-name selection + modified-Enter normalization.
- Modify: `src/Coda.Tui/Ui/Host/TerminalGuiModeRunner.cs` — apply the driver name around `app.Init`, guarded.
- Modify: `src/Coda.Tui/InteractiveProgram.cs` — pass detected driver name into the runner.
- Test: `tests/Coda.Tui.Tests/TerminalInputCompatibilityTests.cs` (new), `TerminalGuiModeRunnerTests.cs`.

### Group E — TUI integration
- Modify: `src/Coda.Tui/Ui/Events/UiEvent.cs` — `UserPromptEnqueuedEvent`, `SteeringDeliveredEvent`, `PendingSteeringRecalledEvent`.
- Modify: `src/Coda.Tui/Ui/State/TranscriptBlock.cs` — `PendingUserTranscriptBlock`.
- Modify: `src/Coda.Tui/Ui/State/UiReducer.cs` — pending append, in-place delivery conversion, recall removal.
- Modify: `src/Coda.Tui/Agent/AgentRunner.cs` — `Steer`, `RecallSteering` pass-throughs; `HasActiveTurn` already present.
- Modify: `src/Coda.Tui/Agent/TuiAgentSink.cs` — `OnSteeringDelivered` publishes `SteeringDeliveredEvent`.
- Modify: `src/Coda.Tui/Ui/TuiController.cs` — busy submission enqueues; seal-race retention.
- Modify: `src/Coda.Tui/Ui/Input/UiActionMap.cs`, `ComposerView.cs`, `ComposerController.cs` — Up-recall precedence + composer restore.
- Modify: `README.md`, `src/Coda.Tui/ImmediateCli.cs` — docs/help.
- Test: `tests/Coda.Tui.Tests/UiReducerTests.cs`, `TuiControllerTests.cs`, `ComposerControllerTests.cs`, `FullscreenTuiShellTests.cs`.

---

# Group A — Rendering foundation (parallel-safe)

Owns block separators, the user-only tool-display setting, the three tool projections, and tiny-mode operational status. All render changes land in the shared formatter/layout/view so inline and fullscreen shells inherit them.

### Task A1: Blank separator row owned by `TranscriptLayoutIndex`

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs` (the `TranscriptRow` record is defined in `TranscriptLayoutIndex.cs:9`; add `IsSeparator`).
- Modify: `src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs`
- Test: `tests/Coda.Tui.Tests/TranscriptLayoutIndexTests.cs`

Context: `TranscriptRow` lives at `TranscriptLayoutIndex.cs:9-16`. Row counts/prefix offsets are maintained in `Append` (`:90-93`), `ReplaceAt` (`:128-131`), `RebuildRowCounts` (`:258-263`); emission is `CollectRows` (`:200-236`) which already skips zero-line blocks (`:206-210`). We make each block that renders ≥1 line occupy `count + 1` rows, the last being an unstyled blank separator.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Coda.Tui.Tests/TranscriptLayoutIndexTests.cs
using System.Collections.Immutable;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class TranscriptLayoutIndexSeparatorTests
{
    private static TranscriptLayoutIndex NewIndex() =>
        new((block, width) => TranscriptBlockFormatter.Format(block, width));

    [Fact]
    public void Each_visible_block_gets_one_trailing_blank_separator_row()
    {
        var index = NewIndex();
        var blocks = ImmutableArray.Create<TranscriptBlock>(
            new UserTranscriptBlock(Guid.NewGuid(), "hello"),
            new UserTranscriptBlock(Guid.NewGuid(), "world"));
        index.ReplaceAll(blocks, width: 40);

        // 1 line + 1 separator per block == 4 rows.
        Assert.Equal(4, index.TotalRows);

        var rows = index.GetRows(0, index.TotalRows);
        Assert.Equal("hello", rows[0].Text);
        Assert.True(rows[1].IsSeparator);
        Assert.Equal(string.Empty, rows[1].Text);
        Assert.False(rows[1].FillWidth);
        Assert.Equal("world", rows[2].Text);
        Assert.True(rows[3].IsSeparator);
    }

    [Fact]
    public void Hidden_zero_line_block_gets_no_separator()
    {
        var index = NewIndex();
        var blocks = ImmutableArray.Create<TranscriptBlock>(
            new AssistantTranscriptBlock(Guid.NewGuid(), string.Empty, Complete: true));
        index.ReplaceAll(blocks, width: 40);

        Assert.Equal(0, index.TotalRows);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptLayoutIndexSeparatorTests"`
Expected: FAIL — `TranscriptRow` has no `IsSeparator` (compile error) / `TotalRows` returns 2 not 4.

- [ ] **Step 3: Add `IsSeparator` to `TranscriptRow`**

In `src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs`, extend the record struct (currently `:9-16`):

```csharp
public readonly record struct TranscriptRow(Guid BlockId, int LocalRow, int GlobalRow, string Text, TranscriptRole Role)
{
    public bool FillWidth { get; init; }

    public string? RightText { get; init; }

    /// <summary>True for the unstyled blank row appended after each visible block. Never selectable or expandable.</summary>
    public bool IsSeparator { get; init; }
}
```

- [ ] **Step 4: Make the layout index reserve and emit the separator row**

In `RebuildRowCounts` (`:253-264`), `Append` (`:82-95`), and `ReplaceAt` (`:114-141`), store the *effective* row count `count > 0 ? count + 1 : 0` instead of the raw `count`. Add a private helper and use it everywhere a formatted count feeds `rowCounts`/`prefix`:

```csharp
private static int EffectiveRows(int formatted) => formatted > 0 ? formatted + 1 : 0;
```

`RebuildRowCounts`:

```csharp
private void RebuildRowCounts()
{
    this.rowCounts.Clear();
    this.prefix.Clear();
    this.prefix.Add(0);
    foreach (var block in this.blocks)
    {
        var count = EffectiveRows(this.formatter(block, this.width).Count);
        this.rowCounts.Add(count);
        this.prefix.Add(this.prefix[^1] + count);
    }
}
```

`Append` (replace `:90-93` `this.rowCounts.Add(lines.Count); this.prefix.Add(this.prefix[^1] + lines.Count);`):

```csharp
var effective = EffectiveRows(lines.Count);
this.blocks = this.blocks.Add(block);
this.rowCounts.Add(effective);
this.prefix.Add(this.prefix[^1] + effective);
this.Cache(block.Id, lines);
```

`ReplaceAt` (replace `:128-131` count/delta computation):

```csharp
this.Evict(this.blocks[position].Id);
var lines = this.Format(block);
var effective = EffectiveRows(lines.Count);
var delta = effective - this.rowCounts[position];
this.blocks = this.blocks.SetItem(position, block);
this.rowCounts[position] = effective;
```

In `CollectRows` (`:200-236`), the last local row of a block (index `count - 1`, where `count` is the effective row count) is the separator. Replace the inner emission loop so it emits real lines for `local < lines.Count` and a separator for `local == count - 1`:

```csharp
var block = this.blocks[blockIndex];
var lines = this.GetLines(blockIndex, block);
for (var local = 0; local < count; local++)
{
    var global = blockStart + local;
    if (global < start)
    {
        continue;
    }

    if (global >= end)
    {
        break;
    }

    if (local >= lines.Count)
    {
        // Trailing separator row: unstyled, never full-width, never selectable/expandable.
        rows.Add(new TranscriptRow(block.Id, local, global, string.Empty, TranscriptRole.Assistant)
        {
            IsSeparator = true,
        });
        continue;
    }

    var line = lines[local];
    rows.Add(new TranscriptRow(block.Id, local, global, line.Text ?? string.Empty, line.Role)
    {
        FillWidth = line.FillWidth,
        RightText = line.RightText,
    });
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptLayoutIndexSeparatorTests"`
Expected: PASS.

- [ ] **Step 6: Run existing layout/shell regression**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptLayoutIndex|FullyQualifiedName~FullscreenTuiShellTests"`
Expected: PASS (row-count assertions in existing shell tests may need +separator adjustments; update any that assert exact `TotalRows`).

- [ ] **Step 7: Commit**

```bash
git add src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs tests/Coda.Tui.Tests/TranscriptLayoutIndexTests.cs
git commit -m "feat(tui): layout index owns blank transcript separator rows

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task A2: Separators are never selectable or expandable

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs`
- Test: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`

Context: click-to-expand is `ToggleExpansionAt` (invoked from `ProcessMouse` around `VirtualizedTranscriptView.cs:434`); position mapping is `ToTranscriptPosition` (`:446-454`). A click on a separator row must be a no-op; selection may include the blank line harmlessly.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs — add
[Fact]
public void Clicking_a_separator_row_does_not_toggle_expansion()
{
    using var fixture = RetainedShellFixture.Create();      // existing helper (RetainedShellFixture.cs)
    var toolBlock = new ToolTranscriptBlock(
        Guid.NewGuid(), "read_file", "{\"path\":\"a.cs\"}", 12, "line1\nline2", false, true);
    fixture.Shell.Transcript.ReplaceAll(ImmutableArray.Create<TranscriptBlock>(toolBlock));

    var expandedBefore = fixture.Shell.Transcript.ExpandedCountForTest;   // add tiny internal accessor
    // The separator is the last row of the tool block.
    fixture.Shell.Transcript.ProcessMouseAtRowForTest(rowIsSeparator: true);

    Assert.Equal(expandedBefore, fixture.Shell.Transcript.ExpandedCountForTest);
}
```

(If exposing test accessors is undesirable, assert instead via `GetRows` that the separator row's `BlockId` is present but `ToggleExpansionAt` early-returns; keep the test at the smallest surface that proves "no toggle".)

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Clicking_a_separator_row"`
Expected: FAIL.

- [ ] **Step 3: Guard expansion against separator rows**

In `VirtualizedTranscriptView.ToggleExpansionAt` (the method reached from `ProcessMouse` at `:434`), resolve the hit `TranscriptRow` and early-return when it is a separator:

```csharp
private void ToggleExpansionAt(Point local)
{
    var globalRow = this.viewport.TopRow + local.Y;
    var row = this.index.GetRows(globalRow, 1);
    if (row.Count == 0 || row[0].IsSeparator)
    {
        return; // blank separator is inert
    }

    // ... existing expansion toggle using row[0].BlockId ...
}
```

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Clicking_a_separator_row"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs
git commit -m "feat(tui): separators are inert for click-expand

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task A3: `ToolDisplayMode` enum + resolver (pure)

**Files:**
- Create: `src/Coda.Tui/Ui/Rendering/ToolDisplayMode.cs`
- Test: `tests/Coda.Tui.Tests/ToolDisplayModeResolverTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Coda.Tui.Tests/ToolDisplayModeResolverTests.cs
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

public sealed class ToolDisplayModeResolverTests
{
    [Theory]
    [InlineData("verbose", ToolDisplayMode.Verbose)]
    [InlineData("Compact", ToolDisplayMode.Compact)]
    [InlineData("TINY", ToolDisplayMode.Tiny)]
    public void Parses_case_insensitively(string raw, ToolDisplayMode expected)
    {
        var mode = ToolDisplayModeResolver.Resolve(raw, out var invalid);
        Assert.Equal(expected, mode);
        Assert.False(invalid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_resolves_to_tiny_without_warning(string? raw)
    {
        var mode = ToolDisplayModeResolver.Resolve(raw, out var invalid);
        Assert.Equal(ToolDisplayMode.Tiny, mode);
        Assert.False(invalid);
    }

    [Fact]
    public void Invalid_resolves_to_tiny_and_flags_warning()
    {
        var mode = ToolDisplayModeResolver.Resolve("loud", out var invalid);
        Assert.Equal(ToolDisplayMode.Tiny, mode);
        Assert.True(invalid);
    }
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ToolDisplayModeResolverTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the enum + resolver**

```csharp
// src/Coda.Tui/Ui/Rendering/ToolDisplayMode.cs
namespace Coda.Tui.Ui.Rendering;

/// <summary>How tool calls/results are projected into the interactive transcript. Data surfaces are unaffected.</summary>
public enum ToolDisplayMode
{
    Verbose,
    Compact,
    Tiny,
}

/// <summary>Resolves the user-configured <c>toolDisplayMode</c> string. Missing/invalid resolve to <see cref="ToolDisplayMode.Tiny"/>.</summary>
public static class ToolDisplayModeResolver
{
    public const int CompactInputPreviewMax = 128;

    public static ToolDisplayMode Resolve(string? raw, out bool wasInvalid)
    {
        wasInvalid = false;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ToolDisplayMode.Tiny;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "verbose": return ToolDisplayMode.Verbose;
            case "compact": return ToolDisplayMode.Compact;
            case "tiny": return ToolDisplayMode.Tiny;
            default:
                wasInvalid = true;
                return ToolDisplayMode.Tiny;
        }
    }
}
```

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ToolDisplayModeResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Ui/Rendering/ToolDisplayMode.cs tests/Coda.Tui.Tests/ToolDisplayModeResolverTests.cs
git commit -m "feat(tui): add ToolDisplayMode enum and resolver

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task A4: User-only `toolDisplayMode` in settings

**Files:**
- Modify: `src/Coda.Agent/Settings/CodaSettings.cs`
- Modify: `src/Coda.Agent/Settings/SettingsLoader.cs`
- Test: `tests/Engine.Tests/Settings/SettingsDefaultsTests.cs`

Context: `SettingsLoader.Load` resolves the user file at `SettingsLoader.cs:61` and the project file at `:62`; the merge (`:67-76`) does `project ?? user`. `toolDisplayMode` MUST be read from the USER file only (bypass the merge). Mirror the optional string-field pattern used by telemetry (`SettingsDocument.Telemetry` DTO + `ParseTelemetry`). `CodaSettings.ToolDisplayMode` holds the RAW string (interpretation happens in the TUI — see Task A3/A8).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Engine.Tests/Settings/SettingsDefaultsTests.cs — add
[Fact]
public void ToolDisplayMode_is_read_from_user_file_only()
{
    this.WriteUser("""{ "toolDisplayMode": "compact" }""");
    this.WriteProject("""{ "toolDisplayMode": "verbose" }""");

    var settings = SettingsLoader.Load(this.projectDir, this.userHome);

    Assert.Equal("compact", settings.ToolDisplayMode);   // project cannot override
}

[Fact]
public void ToolDisplayMode_absent_is_null()
{
    this.WriteUser("""{ }""");
    var settings = SettingsLoader.Load(this.projectDir, this.userHome);
    Assert.Null(settings.ToolDisplayMode);
}
```

(Use the existing `WriteUser`/`WriteProject` helpers already in this test class; if `WriteProject` is absent, add the twin of `WriteUser` writing to `this.projectDir/.coda/settings.json`.)

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ToolDisplayMode_is_read_from_user_file_only|FullyQualifiedName~ToolDisplayMode_absent_is_null"`
Expected: FAIL — `CodaSettings.ToolDisplayMode` does not exist.

- [ ] **Step 3: Add the property to `CodaSettings`**

```csharp
// src/Coda.Agent/Settings/CodaSettings.cs — add inside the record body
/// <summary>Raw user-global tool-display mode string ("verbose"/"compact"/"tiny"); null = not configured.
/// USER settings only — a project settings file cannot set it. Interpreted by the TUI layer.</summary>
public string? ToolDisplayMode { get; init; }
```

- [ ] **Step 4: Parse it in `SettingsLoader` (user file only)**

Add a `ToolDisplayMode` string to the settings DTO (`SettingsDocument`) next to `Telemetry`:

```csharp
[JsonPropertyName("toolDisplayMode")]
public string? ToolDisplayMode { get; set; }
```

In `TryLoadFile`, assign it into the returned `CodaSettings` (raw, no interpretation). In `Load` (the merge, `:67-76`), take it from `userSettings` ONLY:

```csharp
// USER-ONLY: a project settings file must not override tool display mode.
var toolDisplayMode = userSettings.ToolDisplayMode;
```

and include `ToolDisplayMode = toolDisplayMode` in the merged `CodaSettings` constructed at the end of `Load`.

- [ ] **Step 5: Run GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ToolDisplayMode"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Coda.Agent/Settings/CodaSettings.cs src/Coda.Agent/Settings/SettingsLoader.cs tests/Engine.Tests/Settings/SettingsDefaultsTests.cs
git commit -m "feat(settings): user-only toolDisplayMode raw string

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task A5: Mode-aware tool projection in `TranscriptBlockFormatter`

**Files:**
- Modify: `src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs`
- Test: `tests/Coda.Tui.Tests/TranscriptBlockFormatterTests.cs`

Context: `Format` is `static` (`:75`) with a `switch (block)` dispatch (`:82-123`); `AppendTool` (`:328-363`) renders the current *verbose* projection (name + full input JSON + full result). Add an overload `Format(block, width, ToolDisplayMode mode)`; the existing `Format(block, width)` delegates with `ToolDisplayMode.Verbose` to preserve every current test. `tiny` returns zero lines for `ToolTranscriptBlock`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Coda.Tui.Tests/TranscriptBlockFormatterTests.cs — add
using Coda.Tui.Ui.Rendering;

private static ToolTranscriptBlock Tool(string input, string? result) =>
    new(Guid.NewGuid(), "read_file", input, 5, result, IsError: false, Complete: true);

[Fact]
public void Tiny_mode_renders_no_tool_rows()
{
    var lines = TranscriptBlockFormatter.Format(Tool("{\"path\":\"a\"}", "data"), 80, ToolDisplayMode.Tiny);
    Assert.Empty(lines);
}

[Fact]
public void Compact_mode_caps_input_at_128_and_shows_no_result()
{
    var longInput = "{\"path\":\"" + new string('x', 400) + "\"}";
    var lines = TranscriptBlockFormatter.Format(Tool(longInput, "SECRET-RESULT"), 400, ToolDisplayMode.Compact);

    var text = string.Join("\n", lines.Select(l => l.Text));
    Assert.Contains("read_file", text);
    Assert.DoesNotContain("SECRET-RESULT", text);
    Assert.True(lines[0].Text.Length <= "read_file ".Length + ToolDisplayModeResolver.CompactInputPreviewMax + " (5ms)".Length);
}

[Fact]
public void Verbose_mode_preserves_full_input_and_result()
{
    var lines = TranscriptBlockFormatter.Format(Tool("{\"path\":\"a.cs\"}", "full-result-body"), 80, ToolDisplayMode.Verbose);
    var text = string.Join("\n", lines.Select(l => l.Text));
    Assert.Contains("full-result-body", text);
    Assert.Contains("{\"path\":\"a.cs\"}", text);
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptBlockFormatterTests"`
Expected: FAIL — overload missing.

- [ ] **Step 3: Add the mode-aware overload and branch `AppendTool`**

Add the overload and route the existing one through it:

```csharp
public static IReadOnlyList<TranscriptRenderLine> Format(TranscriptBlock block, int width)
    => Format(block, width, ToolDisplayMode.Verbose);

public static IReadOnlyList<TranscriptRenderLine> Format(TranscriptBlock block, int width, ToolDisplayMode toolMode)
{
    var lines = new List<TranscriptRenderLine>();
    var safeWidth = Math.Max(1, width);
    switch (block)
    {
        // ... unchanged cases ...
        case ToolTranscriptBlock tool:
            AppendTool(lines, tool, safeWidth, toolMode);
            break;
        // ... unchanged cases ...
    }

    return lines;
}
```

Change `AppendTool` to accept the mode and project accordingly (keep the existing header/result code as the `Verbose` branch):

```csharp
private static void AppendTool(List<TranscriptRenderLine> lines, ToolTranscriptBlock tool, int width, ToolDisplayMode mode)
{
    if (mode == ToolDisplayMode.Tiny)
    {
        return; // no tool rows in tiny mode
    }

    var role = tool.IsError ? TranscriptRole.Error : TranscriptRole.Tool;

    if (mode == ToolDisplayMode.Compact)
    {
        var preview = SingleLinePreview(tool.InputJson, ToolDisplayModeResolver.CompactInputPreviewMax);
        var state = tool.IsError ? " [error]" : tool.Complete ? " [done]" : " (running)";
        var header = string.IsNullOrEmpty(preview) ? tool.ToolName : $"{tool.ToolName} {preview}";
        AppendPreformatted(lines, header + state, width, role);
        return; // no result content in compact mode
    }

    // Verbose (unchanged behavior)
    var sb = new StringBuilder(tool.ToolName);
    if (!string.IsNullOrWhiteSpace(tool.InputJson))
    {
        sb.Append(' ').Append(tool.InputJson.Trim());
    }

    if (tool.ElapsedMs is { } ms)
    {
        sb.Append(" (").Append(ms.ToString(CultureInfo.InvariantCulture)).Append("ms)");
    }
    else if (!tool.Complete)
    {
        sb.Append(" (running)");
    }

    if (tool.IsError)
    {
        sb.Append(" [error]");
    }

    AppendPreformatted(lines, sb.ToString(), width, role);
    if (tool.Result is { Length: > 0 } result)
    {
        foreach (var line in SplitLines(result))
        {
            foreach (var wrapped in WrapPreformatted(line, width))
            {
                lines.Add(new TranscriptRenderLine(wrapped, role));
            }
        }
    }
}

private static string SingleLinePreview(string? input, int max)
{
    if (string.IsNullOrWhiteSpace(input))
    {
        return string.Empty;
    }

    var oneLine = string.Join(' ', input.Split(['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
}
```

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptBlockFormatterTests"`
Expected: PASS (existing verbose tests still pass via the default overload).

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs tests/Coda.Tui.Tests/TranscriptBlockFormatterTests.cs
git commit -m "feat(tui): verbose/compact/tiny tool projections in formatter

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task A6: Tiny-mode operational status shows generic "Working"

**Files:**
- Modify: `src/Coda.Tui/Ui/State/OperationalStatusProjector.cs`
- Test: `tests/Coda.Tui.Tests/OperationalStatusProjectorTests.cs`

Context: `OperationalStatusProjector.Project(snapshot)` labels the active tool at `:27-31` (`$"Working · {tool.ToolName}"`); a generic `"Working"` already exists at `:42`. Add a `ToolDisplayMode` parameter; in `Tiny`, do not expose the tool name.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Coda.Tui.Tests/OperationalStatusProjectorTests.cs — add
[Fact]
public void Tiny_mode_status_hides_active_tool_name()
{
    var snapshot = UiSessionSnapshot.Empty with
    {
        Transcript = [ new ToolTranscriptBlock(Guid.NewGuid(), "read_file", "{}", null, null, false, false) ],
    };

    var status = OperationalStatusProjector.Project(snapshot, ToolDisplayMode.Tiny);

    Assert.Equal("Working", status.Text);
    Assert.DoesNotContain("read_file", status.Text);
}

[Fact]
public void Verbose_mode_status_keeps_active_tool_name()
{
    var snapshot = UiSessionSnapshot.Empty with
    {
        Transcript = [ new ToolTranscriptBlock(Guid.NewGuid(), "read_file", "{}", null, null, false, false) ],
    };

    var status = OperationalStatusProjector.Project(snapshot, ToolDisplayMode.Verbose);
    Assert.Contains("read_file", status.Text);
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~OperationalStatusProjectorTests"`
Expected: FAIL — `Project` has no mode overload.

- [ ] **Step 3: Thread the mode into the projector**

Add an overload (keep the old signature delegating to `Verbose` so untouched callers compile), and gate the tool label:

```csharp
public static OperationalStatus Project(UiSessionSnapshot snapshot)
    => Project(snapshot, ToolDisplayMode.Verbose);

public static OperationalStatus Project(UiSessionSnapshot snapshot, ToolDisplayMode toolMode)
{
    // ... unchanged prologue ...
    var tool = LastIncompleteTool(snapshot);
    if (tool is not null)
    {
        return toolMode == ToolDisplayMode.Tiny
            ? new("Working", OperationalTone.Working, true)
            : new($"Working · {tool.ToolName}", OperationalTone.Working, true);
    }

    // ... unchanged remainder ...
}
```

Update the projector's single production caller (the operational-status projection site that renders the status line) to pass the resolved mode threaded in Task A8.

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~OperationalStatusProjectorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Ui/State/OperationalStatusProjector.cs tests/Coda.Tui.Tests/OperationalStatusProjectorTests.cs
git commit -m "feat(tui): tiny-mode operational status uses generic Working label

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task A7: Plain interactive fallback honors the mode

**Files:**
- Modify: `src/Coda.Tui/Ui/Rendering/PlainOutputRenderer.cs`
- Test: `tests/Coda.Tui.Tests/PlainOutputRendererTests.cs` (create if absent)

Context: `PlainOutputRenderer.ApplyEventAsync` prints tool events at `:39-50` (`[tool] name input`, `[tool-progress]`, `[tool-result] name: content`). Add a `ToolDisplayMode` ctor field: `tiny` prints no tool lines (progress prints a generic `Working…` at most); `compact` prints name + capped single-line input, no result; `verbose` unchanged.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Coda.Tui.Tests/PlainOutputRendererTests.cs
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Rendering;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class PlainOutputRendererModeTests
{
    private static (PlainOutputRenderer renderer, List<string> lines) New(ToolDisplayMode mode)
    {
        var lines = new List<string>();
        var renderer = new PlainOutputRenderer(lines.Add, mode);   // add mode param
        return (renderer, lines);
    }

    [Fact]
    public async Task Tiny_mode_prints_no_tool_lines()
    {
        var (r, lines) = New(ToolDisplayMode.Tiny);
        await r.ApplyEventAsync(new ToolStartedEvent("read_file", "{\"path\":\"a\"}"));
        await r.ApplyEventAsync(new ToolCompletedEvent("read_file", new ToolResult("data")));
        Assert.DoesNotContain(lines, l => l.Contains("read_file") || l.Contains("data"));
    }

    [Fact]
    public async Task Compact_mode_prints_name_without_result()
    {
        var (r, lines) = New(ToolDisplayMode.Compact);
        await r.ApplyEventAsync(new ToolStartedEvent("read_file", "{\"path\":\"a\"}"));
        await r.ApplyEventAsync(new ToolCompletedEvent("read_file", new ToolResult("SECRET")));
        Assert.Contains(lines, l => l.Contains("read_file"));
        Assert.DoesNotContain(lines, l => l.Contains("SECRET"));
    }
}
```

(Adapt the ctor/`ApplyEventAsync` call to `PlainOutputRenderer`'s real signature; the existing renderer writes via an injected line sink — reuse it.)

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~PlainOutputRendererModeTests"`
Expected: FAIL.

- [ ] **Step 3: Add the mode field and branch the tool cases**

Add a `ToolDisplayMode` ctor parameter (default `Verbose` for existing callers) stored to a field, then branch `:39-50`:

```csharp
case ToolStartedEvent e:
    if (this.toolMode == ToolDisplayMode.Verbose)
    {
        this.WriteLine($"[tool] {e.ToolName} {e.InputJson}");
    }
    else if (this.toolMode == ToolDisplayMode.Compact)
    {
        this.WriteLine($"[tool] {e.ToolName} {ToolPreview(e.InputJson)}");
    }
    // tiny: nothing
    break;

case ToolProgressEvent e:
    if (this.toolMode != ToolDisplayMode.Tiny)
    {
        var seconds = (e.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
        this.WriteLine($"[tool-progress] {(this.toolMode == ToolDisplayMode.Tiny ? "Working" : e.ToolName)} {seconds}s");
    }
    break;

case ToolCompletedEvent e:
    if (this.toolMode == ToolDisplayMode.Verbose)
    {
        this.WriteLine($"[tool-result] {e.ToolName}: {e.Result.Content}");
    }
    // compact + tiny: no result content
    break;
```

with `private static string ToolPreview(string input) => ToolDisplayModeResolver...`-style capping (reuse a 128-char single-line cap).

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~PlainOutputRendererModeTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Ui/Rendering/PlainOutputRenderer.cs tests/Coda.Tui.Tests/PlainOutputRendererTests.cs
git commit -m "feat(tui): plain fallback honors tool display mode

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task A8: Wire the resolved mode into the TUI + invalid-value warning

**Files:**
- Modify: `src/Coda.Tui/InteractiveProgram.cs`
- Modify: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs` (accept the formatter delegate already; see `:40`)
- Test: `tests/Coda.Tui.Tests/InteractiveProgramToolDisplayTests.cs` (create — a thin resolution+warn test)

Context: `SettingsLoader.Load(cwd)` is called at `InteractiveProgram.cs:176`. The shell already takes a `transcriptFormatter` delegate (`FullscreenTuiShell.cs:40`, used `:105`) → `VirtualizedTranscriptView` (`:41-48`) → `TranscriptLayoutIndex`. Resolve the mode once at startup, build the mode-aware formatter closure, pass the mode to the plain renderer and operational-status projection, and warn (once) when the raw value was invalid.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Coda.Tui.Tests/InteractiveProgramToolDisplayTests.cs
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

public sealed class InteractiveProgramToolDisplayTests
{
    [Fact]
    public void Invalid_raw_resolves_tiny_and_yields_a_warning()
    {
        var mode = ToolDisplayModeResolver.Resolve("bogus", out var invalid);
        Assert.Equal(ToolDisplayMode.Tiny, mode);
        Assert.True(invalid);
        // The composition root maps invalid==true to a single startup NotificationEvent/log line.
    }
}
```

(This pins the contract; the composition wiring itself is validated by the shell/formatter integration tests in Groups A/E. If `InteractiveProgram` exposes an internal `ResolveToolDisplayMode` seam, assert on it directly.)

- [ ] **Step 2: Run RED / build**

Run: `dotnet build src\Coda.Tui\Coda.Tui.csproj`
Expected: FAIL until the wiring compiles (the closure/param changes below).

- [ ] **Step 3: Resolve + thread the mode at the composition root**

In `InteractiveProgram.cs`, after `var settings = SettingsLoader.Load(cwd);` (`:176`):

```csharp
var toolMode = ToolDisplayModeResolver.Resolve(settings.ToolDisplayMode, out var toolModeInvalid);
if (toolModeInvalid)
{
    // Existing notice/logging path: surface once at startup, do not block launch.
    events.Publish(new NotificationEvent(
        $"Unknown toolDisplayMode '{settings.ToolDisplayMode}'; using 'tiny'.", UiNotificationLevel.Warning));
}
```

Build the mode-aware formatter closure and pass it where the shell factory currently supplies `TranscriptBlockFormatter.Format`:

```csharp
Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>> formatter =
    (block, width) => TranscriptBlockFormatter.Format(block, width, toolMode);
```

Pass `toolMode` to `new PlainOutputRenderer(..., toolMode)` and to the operational-status projection call (Task A6 overload).

- [ ] **Step 4: Run GREEN + smoke build**

Run: `dotnet build src\Coda.Tui\Coda.Tui.csproj` then `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~InteractiveProgramToolDisplayTests"`
Expected: build succeeds; test PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/InteractiveProgram.cs src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs tests/Coda.Tui.Tests/InteractiveProgramToolDisplayTests.cs
git commit -m "feat(tui): resolve and thread toolDisplayMode; warn on invalid

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

# Group B — Queue engine and APIs (parallel-safe)

Evolves the steering primitive to carry IDs + a seal, delivers queued messages at the earliest safe boundary (including between sequential tools with provider-valid skipped results), adds the delivery notification, and extends serve/task APIs.

### Task B1: Evolve `SteeringInbox` into an ID-bearing, sealable queue

**Files:**
- Modify: `src/Coda.Agent/SteeringInbox.cs`
- Test: `tests/Engine.Tests/SteeringInboxTests.cs` (new)

Context: today `SteeringInbox` wraps `ConcurrentQueue<string>` with `Enqueue(string)`, `Clear()`, `DrainAll()`. Production callers: `AgentLoop.cs:309` (`DrainAll`), `CodaSession.cs:354` (`Enqueue`), `TaskManager.Subagents.cs:202` (`Enqueue`), and recall consumers. We keep the class name to avoid a ~15-site rename, change the element type to `SteeringEntry`, and add seal semantics. `Enqueue` now returns the accepted entry (or `null` when the queue is sealed). `DrainAll` is replaced by `TakeAllForDelivery`; `RecallAll` mirrors it; `TrySealEmpty` closes an empty queue; `Clear` clears AND reopens.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Engine.Tests/SteeringInboxTests.cs
using Coda.Agent;

namespace Engine.Tests;

public sealed class SteeringInboxTests
{
    [Fact]
    public void Enqueue_returns_entry_with_id_and_fifo_take()
    {
        var q = new SteeringInbox();
        var a = q.Enqueue("first");
        var b = q.Enqueue("second");
        Assert.NotNull(a);
        Assert.NotEqual(a!.Id, b!.Id);

        var taken = q.TakeAllForDelivery();
        Assert.Equal(new[] { "first", "second" }, taken.Select(e => e.Text));
        Assert.Empty(q.TakeAllForDelivery());   // drained
    }

    [Fact]
    public void TrySealEmpty_closes_when_empty_and_rejects_further_enqueue()
    {
        var q = new SteeringInbox();
        Assert.True(q.TrySealEmpty());
        Assert.Null(q.Enqueue("late"));         // sealed -> rejected
    }

    [Fact]
    public void TrySealEmpty_fails_when_a_message_is_pending()
    {
        var q = new SteeringInbox();
        q.Enqueue("pending");
        Assert.False(q.TrySealEmpty());
        Assert.Single(q.TakeAllForDelivery());  // still deliverable
    }

    [Fact]
    public void Clear_reopens_and_empties()
    {
        var q = new SteeringInbox();
        q.Enqueue("x");
        q.TrySealEmpty();                        // no-op (pending) but exercise state
        q.Clear();
        Assert.NotNull(q.Enqueue("y"));          // reopened
        Assert.Single(q.TakeAllForDelivery());
    }

    [Fact]
    public void Delivery_and_recall_are_mutually_exclusive_per_entry()
    {
        var q = new SteeringInbox();
        q.Enqueue("only");
        var delivered = q.TakeAllForDelivery();
        var recalled = q.RecallAll();
        Assert.Single(delivered);
        Assert.Empty(recalled);                  // the entry was taken exactly once
    }

    [Fact]
    public void HasPending_reflects_state_without_removing()
    {
        var q = new SteeringInbox();
        Assert.False(q.HasPending);
        q.Enqueue("p");
        Assert.True(q.HasPending);
        Assert.Single(q.TakeAllForDelivery());   // peek did not consume
    }
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SteeringInboxTests"`
Expected: FAIL — new API absent.

- [ ] **Step 3: Reimplement `SteeringInbox`**

```csharp
// src/Coda.Agent/SteeringInbox.cs
namespace Coda.Agent;

/// <summary>An operator steering message queued mid-turn. IDs let the UI and serve orchestrators
/// track delivery vs. recall of an individual entry.</summary>
public sealed record SteeringEntry(string Id, string Text, DateTimeOffset EnqueuedAt);

/// <summary>
/// A thread-safe, ID-bearing queue of steering messages consumed by the running <see cref="AgentLoop"/>.
/// The queue is OPEN while its owning turn/task can still consume steering. <see cref="TrySealEmpty"/>
/// closes it atomically at final turn completion (only when empty); once closed, <see cref="Enqueue"/>
/// is rejected rather than accepted-and-lost. <see cref="Clear"/> re-opens it for the next turn.
/// Delivery (<see cref="TakeAllForDelivery"/>) and recall (<see cref="RecallAll"/>) are mutually
/// exclusive per entry: both remove-and-return under one lock, so an entry is returned exactly once.
/// </summary>
public sealed class SteeringInbox
{
    private readonly object gate = new();
    private readonly List<SteeringEntry> pending = new();
    private bool closed;

    /// <summary>Whether any entry is currently pending (peek; does not remove).</summary>
    public bool HasPending
    {
        get
        {
            lock (this.gate)
            {
                return this.pending.Count > 0;
            }
        }
    }

    /// <summary>Posts a steering message. Returns the accepted entry, or null when the queue is sealed.</summary>
    public SteeringEntry? Enqueue(string comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);
        var entry = new SteeringEntry(Guid.NewGuid().ToString("N"), comment, DateTimeOffset.UtcNow);
        lock (this.gate)
        {
            if (this.closed)
            {
                return null;
            }

            this.pending.Add(entry);
            return entry;
        }
    }

    /// <summary>Removes and returns every pending entry in FIFO order for DELIVERY (empty when none).</summary>
    public IReadOnlyList<SteeringEntry> TakeAllForDelivery() => this.DrainInternal();

    /// <summary>Removes and returns every pending entry in FIFO order for RECALL (empty when none).</summary>
    public IReadOnlyList<SteeringEntry> RecallAll() => this.DrainInternal();

    /// <summary>Atomically closes the queue when empty. Returns false (and stays open) if any entry is pending.</summary>
    public bool TrySealEmpty()
    {
        lock (this.gate)
        {
            if (this.pending.Count > 0)
            {
                return false;
            }

            this.closed = true;
            return true;
        }
    }

    /// <summary>Clears pending entries and re-opens the queue for the next turn/task.</summary>
    public void Clear()
    {
        lock (this.gate)
        {
            this.pending.Clear();
            this.closed = false;
        }
    }

    private IReadOnlyList<SteeringEntry> DrainInternal()
    {
        lock (this.gate)
        {
            if (this.pending.Count == 0)
            {
                return [];
            }

            var drained = this.pending.ToArray();
            this.pending.Clear();
            return drained;
        }
    }
}
```

- [ ] **Step 4: Migrate compile-only call sites (no behavior change yet)**

- `AgentLoop.cs:309`: `var steers = this.steering.DrainAll();` → `var steers = this.steering.TakeAllForDelivery();` and `string.Join("\n\n", steers)` → `string.Join("\n\n", steers.Select(e => e.Text))` (fully rewired in Task B3).
- `CodaSession.cs:354`: `this.steeringInbox.Enqueue(comment);` compiles unchanged (return value ignored for now; changed in B5).
- `TaskManager.Subagents.cs:202`: `t.Steering.Enqueue(message);` compiles unchanged.
- Any test/helper calling `DrainAll()` (e.g. `Tasks/NewTaskToolsTests.cs:90`, `Assert.Contains("tweak the plan", t.Steering!.DrainAll())`) → use `RecallAll().Select(e => e.Text)`.

- [ ] **Step 5: Run GREEN + full engine build**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SteeringInboxTests"` then `dotnet build src\Coda.Agent\Coda.Agent.csproj`
Expected: PASS; build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/Coda.Agent/SteeringInbox.cs src/Coda.Agent/AgentLoop.cs src/Coda.Sdk/CodaSession.cs src/Coda.Agent/Tasks/TaskManager.Subagents.cs tests/Engine.Tests/SteeringInboxTests.cs tests/Engine.Tests/Tasks/NewTaskToolsTests.cs
git commit -m "feat(engine): ID-bearing sealable steering queue

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task B2: `IAgentSink.OnSteeringDelivered` + forwarding

**Files:**
- Modify: `src/Coda.Agent/IAgentSink.cs`
- Modify: `src/Coda.Sdk/RecordingSink.cs`, `src/Coda.Agent/SubagentHost.cs` (CollectingSink), `src/Coda.Agent/Tasks/TaskManager.Subagents.cs` (TaskOutputSink)
- Test: `tests/Engine.Tests/RecordingSinkForwardingTests.cs`

Context: `IAgentSink` (`IAgentSink.cs:9-42`) uses default-interface methods for optional lifecycle events. Adding `OnSteeringDelivered(IReadOnlyList<string> messageIds) { }` as a DEFAULT method means only forwarding decorators need overrides; the many `NullSink` test doubles keep compiling. `RecordingSink` (`RecordingSink.cs`) must forward to `inner`; `CollectingSink`/`TaskOutputSink` forward to `parent`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Engine.Tests/RecordingSinkForwardingTests.cs — add
[Fact]
public void RecordingSink_forwards_OnSteeringDelivered()
{
    var inner = new CapturingInner();
    var sink = new RecordingSink(inner);

    ((IAgentSink)sink).OnSteeringDelivered(new[] { "id-1", "id-2" });

    Assert.Equal(new[] { "id-1", "id-2" }, inner.DeliveredIds);
}
```

Add to the file's `CapturingInner : IAgentSink` test double:

```csharp
public List<string> DeliveredIds { get; } = new();
public void OnSteeringDelivered(IReadOnlyList<string> messageIds) => this.DeliveredIds.AddRange(messageIds);
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~RecordingSink_forwards_OnSteeringDelivered"`
Expected: FAIL — method missing.

- [ ] **Step 3: Add the interface method + forwarders**

`IAgentSink.cs` (after `OnUsage`, `:42`):

```csharp
/// <summary>Fires when queued steering entries were delivered into the turn. Carries their IDs so
/// a UI/orchestrator can reconcile pending state. Default no-op; forwarding sinks override it.</summary>
void OnSteeringDelivered(IReadOnlyList<string> messageIds) { }
```

`RecordingSink.cs` (mirror the `OnLimitReached` forwarder at `:76`):

```csharp
public void OnSteeringDelivered(IReadOnlyList<string> messageIds) => this.inner?.OnSteeringDelivered(messageIds);
```

`SubagentHost.cs` CollectingSink (`:163`, mirror `OnLimitReached` `:191`):

```csharp
public void OnSteeringDelivered(IReadOnlyList<string> messageIds) => this.parent.OnSteeringDelivered(messageIds);
```

`TaskManager.Subagents.cs` TaskOutputSink (`:248`, mirror the parent forwarder `:293-296`):

```csharp
public void OnSteeringDelivered(IReadOnlyList<string> messageIds) => this._parent.OnSteeringDelivered(messageIds);
```

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~RecordingSinkForwardingTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Agent/IAgentSink.cs src/Coda.Sdk/RecordingSink.cs src/Coda.Agent/SubagentHost.cs src/Coda.Agent/Tasks/TaskManager.Subagents.cs tests/Engine.Tests/RecordingSinkForwardingTests.cs
git commit -m "feat(engine): IAgentSink.OnSteeringDelivered forwarding

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task B3: Deliver queued steering at safe boundaries + seal at completion

**Files:**
- Modify: `src/Coda.Agent/AgentLoop.cs`
- Test: `tests/Engine.Tests/AgentLoopTests.cs`

Context: the top-of-iteration steering seam is `AgentLoop.cs:303-315`; the natural text-only stop returns the turn at `:452-547` (`return; // turn complete` `:546`). We (a) make the top-of-loop drain fire `OnSteeringDelivered`, (b) refuse to seal-complete while steering is pending (text-only natural stop keeps the turn alive per spec §3), sealing via `TrySealEmpty`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Engine.Tests/AgentLoopTests.cs — add (mirror the existing steering test at :268)
[Fact]
public async Task Before_request_steering_fires_delivered_notification()
{
    var inbox = new SteeringInbox();
    var turn1 = new[]
    {
        AssistantStreamEvent.Tool(new ToolUseBlock("tu", "steer_now", "{}")),
        AssistantStreamEvent.Finished("tool_use"),
    };
    var turn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

    var sink = new RecordingSink();
    var loop = new AgentLoop(
        new ScriptedClient(turn1, turn2),
        new ToolRegistry([new SteeringTool(inbox, "focus tests")]),
        new AllowAllPermissionPrompt(),
        Options(),
        steering: inbox);

    await loop.RunAsync(new List<ChatMessage> { ChatMessage.UserText("hi") }, sink, CancellationToken.None);

    Assert.Single(sink.Delivered);           // one delivery batch
    Assert.Single(sink.Delivered[0]);        // one id
}

[Fact]
public async Task Text_only_stop_keeps_turn_alive_while_steering_pending()
{
    var inbox = new SteeringInbox();
    // Turn 1: pure text stop, but a steer is posted before turn 1 ends (via a tool in turn 1).
    var turn1 = new[]
    {
        AssistantStreamEvent.Tool(new ToolUseBlock("tu", "steer_now", "{}")),
        AssistantStreamEvent.Finished("tool_use"),
    };
    var turn2 = new[] { AssistantStreamEvent.Finished("end_turn") };
    var turn3 = new[] { AssistantStreamEvent.Finished("end_turn") };

    var loop = new AgentLoop(
        new ScriptedClient(turn1, turn2, turn3),
        new ToolRegistry([new SteeringTool(inbox, "one more thing")]),
        new AllowAllPermissionPrompt(),
        Options(),
        steering: inbox);

    var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
    await loop.RunAsync(history, new NullSink(), CancellationToken.None);

    Assert.Contains(history, m => m.Role == ChatRole.User
        && m.Content.OfType<TextBlock>().Any(t => t.Text.Contains("one more thing")));
}
```

Extend the file's `RecordingSink` test double with:

```csharp
public List<IReadOnlyList<string>> Delivered { get; } = new();
public void OnSteeringDelivered(IReadOnlyList<string> messageIds) => this.Delivered.Add(messageIds);
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~Before_request_steering_fires_delivered_notification|FullyQualifiedName~Text_only_stop_keeps_turn_alive"`
Expected: FAIL.

- [ ] **Step 3: Update the top-of-loop drain to fire the notification**

Replace the seam body at `AgentLoop.cs:307-315`:

```csharp
if (this.steering is not null)
{
    var steers = this.steering.TakeAllForDelivery();
    if (steers.Count > 0)
    {
        var steerText = string.Join("\n\n", steers.Select(e => e.Text));
        history.Add(new ChatMessage(ChatRole.User, [new TextBlock(steerText)]));
        sink.OnSteeringDelivered(steers.Select(e => e.Id).ToArray());
    }
}
```

- [ ] **Step 4: Refuse to seal-complete while steering is pending**

Immediately before `return; // turn complete` (`:546`), insert:

```csharp
// EARLIEST SAFE DELIVERY (text-only natural stop): if steering is still pending, keep the turn
// alive rather than completing — the next iteration's steering seam delivers it. TrySealEmpty
// atomically closes the queue only when empty, so no late message is lost at the boundary.
if (this.steering is not null && !this.steering.TrySealEmpty())
{
    continue;
}

sink.OnStopReason(stopReason);          // NOTE: keep the existing OnStopReason/OnLimitReached lines
```

(Keep the existing `sink.OnStopReason(stopReason);` and `max_tokens` block that already sit just above the return; place the seal check before them so a pending steer re-runs the loop before any stop is reported. Re-order so the seal check runs first.)

- [ ] **Step 5: Run GREEN + steering regression**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~AgentLoopTests"`
Expected: PASS (including the pre-existing `Steering_comment_is_injected_before_the_next_model_call`).

- [ ] **Step 6: Commit**

```bash
git add src/Coda.Agent/AgentLoop.cs tests/Engine.Tests/AgentLoopTests.cs
git commit -m "feat(engine): deliver steering at safe boundary; seal turn atomically

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task B4: Skip not-yet-started tools with provider-valid results

**Files:**
- Modify: `src/Coda.Agent/AgentLoop.cs`
- Test: `tests/Engine.Tests/AgentLoopTests.cs`

Context: `RunToolsAsync` (`:591-753`) runs tools in `foreach (var toolUse in toolUses)` (`:615`). Each tool_use is answered by `new ToolResultBlock(toolUse.Id, content, isError)` (`:625/643/655/740`). Convert to an indexed `for`, and at the TOP of each iteration, if steering is pending, emit error-shaped skipped results for the current and all remaining tools, append the combined steering text to the same user message, fire `OnSteeringDelivered`, and break. This covers "during model streaming" (i==0 → skip all) and "during a tool call" (i==k+1 → skip the rest); the currently running tool already finished in the prior iteration.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Engine.Tests/AgentLoopTests.cs — add (mirror the two-tool batch test at :142)
[Fact]
public async Task Steering_between_tools_skips_remaining_with_error_results()
{
    var inbox = new SteeringInbox();
    // Turn 1 requests TWO tools: tu1 posts a steer, tu2 must be skipped.
    var turn1 = new[]
    {
        AssistantStreamEvent.Tool(new ToolUseBlock("tu1", "steer_now", "{}")),
        AssistantStreamEvent.Tool(new ToolUseBlock("tu2", "should_not_run", "{}")),
        AssistantStreamEvent.Finished("tool_use"),
    };
    var turn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

    var ran = new List<string>();
    var loop = new AgentLoop(
        new ScriptedClient(turn1, turn2),
        new ToolRegistry([
            new SteeringTool(inbox, "stop and refocus"),
            new RecordingTool("should_not_run", ran),
        ]),
        new AllowAllPermissionPrompt(),
        Options(),
        steering: inbox);

    var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
    var sink = new RecordingSink();
    await loop.RunAsync(history, sink, CancellationToken.None);

    Assert.DoesNotContain("should_not_run", ran);                 // tu2 never executed
    var toolResults = history.SelectMany(m => m.Content).OfType<ToolResultBlock>().ToList();
    var tu2Result = toolResults.Single(r => r.ToolUseId == "tu2");
    Assert.True(tu2Result.IsError);
    Assert.Contains("steering", tu2Result.Content, StringComparison.OrdinalIgnoreCase);
    Assert.Contains(history, m => m.Content.OfType<TextBlock>().Any(t => t.Text.Contains("stop and refocus")));
}
```

Add a `RecordingTool` double if absent (a read-only `ITool` whose `ExecuteAsync` records its name into the supplied list and returns `new ToolResult("ok")`).

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~Steering_between_tools_skips_remaining"`
Expected: FAIL — tu2 runs; no skipped result.

- [ ] **Step 3: Convert to indexed loop + top-of-iteration steering guard**

Change the loop header (`:615`) to `for (var i = 0; i < toolUses.Count; i++) { var toolUse = toolUses[i];` and insert the guard as the first statement of the body:

```csharp
for (var i = 0; i < toolUses.Count; i++)
{
    var toolUse = toolUses[i];

    // EARLIEST SAFE DELIVERY (mid-batch): operator steering arrived. Do not start this or any
    // later tool. Emit provider-valid error results so every tool_use is answered, append the
    // combined steering text to THIS user message, and stop the batch. The provider invariant
    // (every tool_use answered in the immediately-following user message) is preserved.
    if (this.steering is not null && this.steering.HasPending)
    {
        for (var s = i; s < toolUses.Count; s++)
        {
            var skipped = toolUses[s];
            var skippedResult = new ToolResult(
                "Skipped: not executed because new operator steering arrived before this tool started.",
                IsError: true);
            sink.OnToolResult(skipped.Name, skippedResult);
            results.Add(new ToolResultBlock(skipped.Id, skippedResult.Content, skippedResult.IsError));
        }

        var delivered = this.steering.TakeAllForDelivery();
        if (delivered.Count > 0)
        {
            results.Add(new TextBlock(string.Join("\n\n", delivered.Select(e => e.Text))));
            sink.OnSteeringDelivered(delivered.Select(e => e.Id).ToArray());
        }

        break;
    }

    sink.OnToolCall(toolUse.Name, toolUse.InputJson);
    // ... existing per-tool execution unchanged ...
}
```

(`results` is `List<ContentBlock>`, so appending a `TextBlock` is valid; the caller wraps `results` as the follow-up user message at `:554`.)

- [ ] **Step 4: Run GREEN + full AgentLoop regression**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~AgentLoopTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Agent/AgentLoop.cs tests/Engine.Tests/AgentLoopTests.cs
git commit -m "feat(engine): skip not-yet-started tools with provider-valid results on steering

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task B5: `CodaSession` returns the entry id; reopens the queue per turn

**Files:**
- Modify: `src/Coda.Sdk/CodaSession.cs`
- Test: `tests/Engine.Tests/Sdk/CodaSessionLoopFactoryTests.cs` (or nearest CodaSession test)

Context: `CodaSession.Steer` (`:351-355`) is `void`; `ClearSteering` (`:361`) calls `steeringInbox.Clear()`. The inbox is owned at `:61` and passed into the loop spec at `:383`. Make `Steer` return the accepted entry id (or null), add `RecallSteering()`, and reopen the queue at turn start.

- [ ] **Step 1: Write the failing test**

```csharp
// nearest CodaSession test file — add
[Fact]
public void Steer_returns_entry_id_and_recall_returns_entries()
{
    var session = /* build a CodaSession via existing test helper */;
    var id = session.Steer("do X");
    Assert.False(string.IsNullOrWhiteSpace(id));

    var recalled = session.RecallSteering();
    Assert.Single(recalled);
    Assert.Equal("do X", recalled[0].Text);
    Assert.Equal(id, recalled[0].Id);
    Assert.Empty(session.RecallSteering());   // atomically cleared
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~Steer_returns_entry_id_and_recall_returns_entries"`
Expected: FAIL.

- [ ] **Step 3: Update `CodaSession`**

```csharp
public string? Steer(string comment)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(comment);
    return this.steeringInbox.Enqueue(comment)?.Id;
}

public void ClearSteering() => this.steeringInbox.Clear();

/// <summary>Atomically removes and returns all still-pending steering entries in FIFO order.</summary>
public IReadOnlyList<SteeringEntry> RecallSteering() => this.steeringInbox.RecallAll();
```

At turn start inside `RunAsync` (just before building the loop spec at `:381`), reopen the queue so a turn always begins with an open, empty queue:

```csharp
this.steeringInbox.Clear(); // reopen for this turn (a sealed queue from a prior turn would reject steering)
```

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~CodaSession"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Sdk/CodaSession.cs tests/Engine.Tests/Sdk/CodaSessionLoopFactoryTests.cs
git commit -m "feat(sdk): CodaSession.Steer returns id; RecallSteering; per-turn reopen

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task B6: Serve `session/steer` messageId (backward-compatible)

**Files:**
- Modify: `src/Coda.Sdk/Serve/Messages/SteerResult.cs`
- Modify: `src/Coda.Sdk/Serve/ServeHost.cs`
- Test: `tests/Engine.Tests/Serve/ServeProtocolTests.cs`, `tests/Engine.Tests/Serve/ServeHostTests.cs`

Context: `SteerResult` is `record SteerResult([property: JsonPropertyName("ok")] bool Ok)` (`SteerResult.cs:6-7`). `ServeJson.Options` uses camelCase + `WhenWritingNull` (`ServeJson.cs:9-13`), so a nullable trailing `MessageId` is omitted when null and existing `{ "ok": ... }` payloads still bind. The handler is `ServeHost.cs:501-519`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Engine.Tests/Serve/ServeProtocolTests.cs — extend
[Fact]
public void SteerResult_round_trips_with_message_id()
{
    var original = new SteerResult(true, "queue-entry-7");
    var result = RoundTrip(original);
    Assert.True(result.Ok);
    Assert.Equal("queue-entry-7", result.MessageId);
}

[Fact]
public void SteerResult_omits_message_id_when_null()
{
    var node = ServeJson.ToNode(new SteerResult(false));
    Assert.False(node!.AsObject().ContainsKey("messageId"));   // WhenWritingNull
    Assert.False(ServeJson.FromNode<SteerResult>(node)!.Ok);   // legacy {ok:false} still binds
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SteerResult_round_trips_with_message_id|FullyQualifiedName~SteerResult_omits_message_id"`
Expected: FAIL — `MessageId` absent.

- [ ] **Step 3: Add `MessageId` + return the accepted id**

`SteerResult.cs`:

```csharp
public sealed record SteerResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("messageId")] string? MessageId = null);
```

`ServeHost.cs` steer handler (`:501-519`) — capture the id from `Steer`:

```csharp
conn.OnRequest(ServeMethods.Steer, p =>
{
    this.EnsureAuthenticated();
    var sp = ServeJson.FromNode<SteerParams>(p);
    string? messageId = null;
    if (!string.IsNullOrWhiteSpace(sp?.Text))
    {
        lock (this.turnLock)
        {
            if (this.turnRunning == 1)
            {
                messageId = sess.Steer(sp!.Text);   // null if the queue is already sealed
            }
        }
    }

    return ServeJson.ToNode(new SteerResult(messageId is not null, messageId));
});
```

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ServeProtocolTests|FullyQualifiedName~Serve.ServeHostTests"`
Expected: PASS (existing `Steer_when_no_turn_running_is_rejected...` still asserts `ok == false`).

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Sdk/Serve/Messages/SteerResult.cs src/Coda.Sdk/Serve/ServeHost.cs tests/Engine.Tests/Serve/ServeProtocolTests.cs
git commit -m "feat(serve): session/steer returns accepted messageId

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task B7: Serve `session/recallSteering` method

**Files:**
- Create: `src/Coda.Sdk/Serve/Messages/RecallSteeringResult.cs`, `src/Coda.Sdk/Serve/Messages/RecalledSteeringMessage.cs`
- Modify: `src/Coda.Sdk/Serve/ServeMethods.cs`, `src/Coda.Sdk/Serve/ServeHost.cs`
- Test: `tests/Engine.Tests/Serve/ServeProtocolTests.cs`, `tests/Engine.Tests/Serve/ServeHostTests.cs`

Context: methods are registered via `conn.OnRequest(ServeMethods.X, handler)` (pattern at `:489-494/501-519`); constants live in `ServeMethods.cs:8-16`; there is a `ServeMethods_constants_have_expected_values` protocol test to extend. Recall while no entries pending succeeds with an empty array; auth + running-session guards apply.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Engine.Tests/Serve/ServeProtocolTests.cs — extend
[Fact]
public void RecallSteeringResult_round_trips()
{
    var original = new RecallSteeringResult(new[]
    {
        new RecalledSteeringMessage("id-1", "fix parser", DateTimeOffset.Parse("2026-07-22T04:00:00Z")),
    });
    var result = RoundTrip(original);
    Assert.Single(result.Messages);
    Assert.Equal("fix parser", result.Messages[0].Text);
}

[Fact]
public void RecallSteering_method_constant_is_expected()
{
    Assert.Equal("session/recallSteering", ServeMethods.RecallSteering);
}
```

Plus a host test in `ServeHostTests.cs`: start a turn, `session/steer` twice, `session/recallSteering` returns both in order and a second recall returns empty.

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~RecallSteering"`
Expected: FAIL.

- [ ] **Step 3: Add the message records, method constant, and handler**

```csharp
// src/Coda.Sdk/Serve/Messages/RecalledSteeringMessage.cs
using System.Text.Json.Serialization;
namespace Coda.Sdk.Serve.Messages;

public sealed record RecalledSteeringMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("enqueuedAt")] DateTimeOffset EnqueuedAt);
```

```csharp
// src/Coda.Sdk/Serve/Messages/RecallSteeringResult.cs
using System.Text.Json.Serialization;
namespace Coda.Sdk.Serve.Messages;

public sealed record RecallSteeringResult(
    [property: JsonPropertyName("messages")] IReadOnlyList<RecalledSteeringMessage> Messages);
```

`ServeMethods.cs` (with the request constants):

```csharp
public const string RecallSteering = "session/recallSteering";
```

`ServeHost.cs` (register beside the steer handler):

```csharp
conn.OnRequest(ServeMethods.RecallSteering, _ =>
{
    this.EnsureAuthenticated();
    IReadOnlyList<SteeringEntry> entries = [];
    lock (this.turnLock)
    {
        if (this.turnRunning == 1)
        {
            entries = sess.RecallSteering();
        }
    }

    var messages = entries
        .Select(e => new RecalledSteeringMessage(e.Id, e.Text, e.EnqueuedAt))
        .ToArray();
    return ServeJson.ToNode(new RecallSteeringResult(messages));
});
```

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~RecallSteering|FullyQualifiedName~ServeMethods_constants"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Sdk/Serve/Messages/RecallSteeringResult.cs src/Coda.Sdk/Serve/Messages/RecalledSteeringMessage.cs src/Coda.Sdk/Serve/ServeMethods.cs src/Coda.Sdk/Serve/ServeHost.cs tests/Engine.Tests/Serve/ServeProtocolTests.cs tests/Engine.Tests/Serve/ServeHostTests.cs
git commit -m "feat(serve): session/recallSteering method

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task B8: Serve `event/steeringDelivered` + `WireAgentSink`

**Files:**
- Create: `src/Coda.Sdk/Serve/Messages/SteeringDeliveredEvent.cs`
- Modify: `src/Coda.Sdk/Serve/ServeMethods.cs`, `src/Coda.Sdk/Serve/WireAgentSink.cs`
- Test: `tests/Engine.Tests/Serve/ServeProtocolTests.cs`, `tests/Engine.Tests/Serve/ServeHostTests.cs`

Context: events are emitted by `WireAgentSink` via `SendAsync(ServeMethods.EventX, node)` (`WireAgentSink.cs:58-62/76-86`); event constants at `ServeMethods.cs:19-30`. Mirror `LimitReachedEvent`.

- [ ] **Step 1: Write the failing tests**

```csharp
// ServeProtocolTests.cs — extend
[Fact]
public void SteeringDeliveredEvent_round_trips()
{
    var original = new SteeringDeliveredEvent(new[] { "id-1", "id-2" });
    var result = RoundTrip(original);
    Assert.Equal(new[] { "id-1", "id-2" }, result.MessageIds);
}

[Fact]
public void EventSteeringDelivered_constant_is_expected()
{
    Assert.Equal("event/steeringDelivered", ServeMethods.EventSteeringDelivered);
}
```

Plus a `ServeHostTests` assertion that `WireAgentSink.OnSteeringDelivered([...])` emits an `event/steeringDelivered` notification (reuse the existing notification-capture harness used for `event/limitReached`).

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SteeringDeliveredEvent|FullyQualifiedName~EventSteeringDelivered_constant"`
Expected: FAIL.

- [ ] **Step 3: Add the event record, constant, and wire forwarder**

```csharp
// src/Coda.Sdk/Serve/Messages/SteeringDeliveredEvent.cs
using System.Text.Json.Serialization;
namespace Coda.Sdk.Serve.Messages;

public sealed record SteeringDeliveredEvent(
    [property: JsonPropertyName("messageIds")] IReadOnlyList<string> MessageIds);
```

`ServeMethods.cs` (with the event constants):

```csharp
public const string EventSteeringDelivered = "event/steeringDelivered";
```

`WireAgentSink.cs` (mirror `OnLimitReached` `:58-62`):

```csharp
public void OnSteeringDelivered(IReadOnlyList<string> messageIds)
{
    var node = ServeJson.ToNode(new SteeringDeliveredEvent(messageIds));
    _ = this.SendAsync(ServeMethods.EventSteeringDelivered, node);
}
```

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SteeringDelivered|FullyQualifiedName~Serve.ServeHostTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Sdk/Serve/Messages/SteeringDeliveredEvent.cs src/Coda.Sdk/Serve/ServeMethods.cs src/Coda.Sdk/Serve/WireAgentSink.cs tests/Engine.Tests/Serve/ServeProtocolTests.cs tests/Engine.Tests/Serve/ServeHostTests.cs
git commit -m "feat(serve): event/steeringDelivered notification

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task B9: `TaskManager.RecallSteering` + deferred `task_recall` tool

**Files:**
- Modify: `src/Coda.Agent/Tasks/TaskManager.Subagents.cs`
- Create: `src/Coda.Agent/Tools/TaskRecallTool.cs`
- Modify: `src/Coda.Agent/Tools/BuiltInTools.cs`
- Test: `tests/Engine.Tests/Tasks/NewTaskToolsTests.cs`, `tests/Engine.Tests/ToolSearch/DeferralTests.cs`

Context: `TaskManager.Steer(id, message, callerTaskId)` (`:194-204`) applies the guard order Find → `IsAuthorizedCaller` → Kind (subagent/scheduled) → Running → Steering!=null. NotFound and Denied are indistinguishable (`TaskSendTool.cs:50`). `TaskSendTool` is the template; the ONLY behavioral difference for `task_recall` is `ShouldDefer => true` (spec §9 requires deferred discovery — see Inconsistency #3). Register in `BuiltInTools.All()` (`BuiltInTools.cs:6-36`, `new TaskSendTool()` at `:29`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Engine.Tests/Tasks/NewTaskToolsTests.cs — add (mirror :82-91)
[Fact]
public async Task Task_recall_returns_and_clears_pending_for_authorized_caller()
{
    var mgr = NewManager();
    var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
    var inbox = new SteeringInbox();
    t.AttachSteering(inbox);
    inbox.Enqueue("tweak the plan");

    var result = await new TaskRecallTool().ExecuteAsync(
        Input($$"""{"task_id":"{{t.Id}}"}"""), Ctx(mgr), CancellationToken.None);

    Assert.Contains("tweak the plan", result.Content);
    Assert.Empty(inbox.RecallAll());   // cleared
}

[Fact]
public async Task Task_recall_unauthorized_is_indistinguishable_from_not_found()
{
    var mgr = NewManager();
    var branchA = mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
    var branchB = mgr.Register(TaskKind.Subagent, "b", parentTaskId: null);
    branchB.AttachSteering(new SteeringInbox());

    // caller is branchA (not an ancestor of branchB) -> must read as "not found"
    var result = await new TaskRecallTool().ExecuteAsync(
        Input($$"""{"task_id":"{{branchB.Id}}"}"""), Ctx(mgr, callerTaskId: branchA.Id), CancellationToken.None);

    Assert.Contains("not found", result.Content);
}
```

```csharp
// tests/Engine.Tests/ToolSearch/DeferralTests.cs — add
[Fact]
public void Task_recall_is_deferred()
{
    Assert.True(DeferredTools.IsDeferred(new TaskRecallTool()));
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~Task_recall"`
Expected: FAIL — type + method absent.

- [ ] **Step 3: Add `RecallSteering` to `TaskManager` (same guard order as `Steer`)**

```csharp
// src/Coda.Agent/Tasks/TaskManager.Subagents.cs — beside Steer (:194)
public (TaskActionResult Status, IReadOnlyList<SteeringEntry> Messages) RecallSteering(string id, string? callerTaskId)
{
    var t = Find(id);
    if (t is null) return (TaskActionResult.NotFound, []);
    if (!IsAuthorizedCaller(id, callerTaskId)) return (TaskActionResult.Denied, []);
    if (t.Kind is not (TaskKind.Subagent or TaskKind.Scheduled)) return (TaskActionResult.Rejected, []);
    if (t.Status != TaskRunStatus.Running) return (TaskActionResult.InvalidState, []);
    if (t.Steering is null) return (TaskActionResult.Rejected, []);
    return (TaskActionResult.Ok, t.Steering.RecallAll());
}

public (TaskActionResult Status, IReadOnlyList<SteeringEntry> Messages) RecallSteering(string id)
    => RecallSteering(id, callerTaskId: null);
```

- [ ] **Step 4: Add the deferred `task_recall` tool + register it**

```csharp
// src/Coda.Agent/Tools/TaskRecallTool.cs
using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>Recalls (removes and returns) the still-pending steering messages queued for a running
/// agent task. Deferred: surfaced via tool_search like other advanced task tools.</summary>
public sealed class TaskRecallTool : ITool
{
    public string Name => "task_recall";

    public string Description =>
        "Recall (remove and return) the still-pending steering messages you queued for a running agent task (subagent or scheduled). Shell tasks cannot be steered or recalled.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The agent task id (subagent or scheduled)"}},"required":["task_id"]}
        """;

    public bool IsReadOnly => true;

    public bool ShouldDefer => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null)
        {
            return Task.FromResult(new ToolResult("Tasks are not available in this context.", IsError: false));
        }

        var taskId = ToolInput.GetString(input, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(new ToolResult("Missing required 'task_id'.", IsError: true));
        }

        var (status, messages) = context.Tasks.RecallSteering(taskId, context.CurrentTaskId);
        return Task.FromResult(status switch
        {
            TaskActionResult.Ok => new ToolResult(messages.Count == 0
                ? $"No pending steering messages for task '{taskId}'."
                : $"Recalled {messages.Count} message(s) from task '{taskId}':\n" +
                  string.Join("\n", messages.Select(m => $"- {m.Text}"))),
            // Same wording as NotFound so a caller cannot distinguish an unauthorized target.
            TaskActionResult.NotFound or TaskActionResult.Denied => new ToolResult($"Task '{taskId}' not found."),
            TaskActionResult.InvalidState => new ToolResult($"Task '{taskId}' is not running and has no steering to recall."),
            _ => new ToolResult($"Task '{taskId}' cannot be steered (only running agent tasks accept messages)."),
        });
    }
}
```

Register in `BuiltInTools.cs` (add beside `new TaskSendTool()` `:29`):

```csharp
new TaskRecallTool(),
```

- [ ] **Step 5: Run GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~Task_recall|FullyQualifiedName~DeferralTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Coda.Agent/Tasks/TaskManager.Subagents.cs src/Coda.Agent/Tools/TaskRecallTool.cs src/Coda.Agent/Tools/BuiltInTools.cs tests/Engine.Tests/Tasks/NewTaskToolsTests.cs tests/Engine.Tests/ToolSearch/DeferralTests.cs
git commit -m "feat(tasks): authorization-aware task_recall deferred tool

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

# Group C — Viewport UX (parallel-safe)

Adds the block-counted jump-to-bottom hint, a shell-global Ctrl+End, and an interactive right-side scrollbar. Implemented in `FullscreenTuiShell`/`VirtualizedTranscriptView`/`TranscriptViewportState` so `InlineTuiShell` (which extends `FullscreenTuiShell`) inherits them.

### Task C1: Block-level unseen counter in `TranscriptViewportState`

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs`
- Test: `tests/Coda.Tui.Tests/TranscriptViewportStateTests.cs` (new)

Context (Inconsistency #2): `UnseenRows` (`:18`, updated in `OnRowsAppended` `:80`) counts wrapped ROWS and is fed by streaming growth. Add a SEPARATE `UnseenBlocks` counter incremented only by whole visible-block appends and cleared by `JumpToNewest`/reaching bottom. Do not disturb `UnseenRows`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Coda.Tui.Tests/TranscriptViewportStateTests.cs
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

public sealed class TranscriptViewportStateTests
{
    private static TranscriptViewportState Scrolled()
    {
        var s = new TranscriptViewportState();
        s.SetViewportHeight(5);
        s.SetContentRows(100);
        s.ScrollBy(-50);          // scroll up -> AutoFollow off
        return s;
    }

    [Fact]
    public void Block_append_increments_unseen_blocks_only_when_scrolled_away()
    {
        var s = Scrolled();
        s.OnBlockAppended();
        s.OnBlockAppended();
        Assert.Equal(2, s.UnseenBlocks);
    }

    [Fact]
    public void Streaming_row_growth_does_not_change_unseen_blocks()
    {
        var s = Scrolled();
        s.OnRowsAppended(4);      // streaming growth of an existing block
        Assert.Equal(0, s.UnseenBlocks);
    }

    [Fact]
    public void Jump_to_newest_clears_unseen_blocks()
    {
        var s = Scrolled();
        s.OnBlockAppended();
        s.JumpToNewest();
        Assert.Equal(0, s.UnseenBlocks);
        Assert.True(s.AutoFollow);
    }

    [Fact]
    public void Auto_following_append_keeps_unseen_blocks_zero()
    {
        var s = new TranscriptViewportState();
        s.SetViewportHeight(5);
        s.SetContentRows(3);      // fits -> AutoFollow stays true
        s.OnBlockAppended();
        Assert.Equal(0, s.UnseenBlocks);
    }
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptViewportStateTests"`
Expected: FAIL — `UnseenBlocks`/`OnBlockAppended` absent.

- [ ] **Step 3: Add the counter**

```csharp
// TranscriptViewportState.cs — add near UnseenRows (:18)
/// <summary>Count of newly appended VISIBLE transcript blocks since the viewport last reached bottom.
/// Distinct from <see cref="UnseenRows"/>: streaming growth of an existing block never changes it.</summary>
public int UnseenBlocks { get; private set; }

/// <summary>Records that one whole visible block was appended. Increments <see cref="UnseenBlocks"/>
/// only while scrolled away (auto-follow off).</summary>
public void OnBlockAppended()
{
    if (!this.AutoFollow)
    {
        this.UnseenBlocks++;
    }
}
```

Clear it wherever the viewport reaches bottom / follows: in `JumpToNewest` (`:102-107`) add `this.UnseenBlocks = 0;`; in `ScrollBy` (`:47`) where `TopRow >= MaxTopRow` sets `AutoFollow = true; UnseenRows = 0;` (`:63-64`) add `this.UnseenBlocks = 0;`; in `OnRowsAppended` (`:80`) where `AutoFollow` resets unseen (`:88`) add `this.UnseenBlocks = 0;`.

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptViewportStateTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs tests/Coda.Tui.Tests/TranscriptViewportStateTests.cs
git commit -m "feat(tui): block-level unseen counter for jump hint

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task C2: Feed block appends from the view (not streaming growth)

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs`
- Test: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`

Context: `VirtualizedTranscriptView.Append` (`:115-123`) adds a whole block and feeds `viewport.OnRowsAppended(delta)`; `ReplaceBlock` (`:139-163`) feeds streaming deltas (`:152`). Call `viewport.OnBlockAppended()` from `Append` only, and only when the appended block renders ≥1 row (hidden tiny-mode tools render 0 rows → no unseen increment, per spec).

- [ ] **Step 1: Write the failing test**

```csharp
// FullscreenTuiShellTests.cs — add
[Fact]
public void Appending_a_visible_block_while_scrolled_increments_unseen_blocks()
{
    using var fixture = RetainedShellFixture.Create();
    var view = fixture.Shell.Transcript;
    view.SetViewportHeightForTest(3);                    // existing/added test seam
    view.ReplaceAll(ImmutableArray.Create<TranscriptBlock>(
        new UserTranscriptBlock(Guid.NewGuid(), "a"),
        new UserTranscriptBlock(Guid.NewGuid(), "b"),
        new UserTranscriptBlock(Guid.NewGuid(), "c"),
        new UserTranscriptBlock(Guid.NewGuid(), "d")));
    view.ScrollBy(-3);                                   // scroll away

    view.Append(new UserTranscriptBlock(Guid.NewGuid(), "new visible"));

    Assert.Equal(1, view.UnseenBlocks);                  // expose UnseenBlocks passthrough
}
```

Add a passthrough `public int UnseenBlocks => this.viewport.UnseenBlocks;` next to `UnseenRows` (`:79`).

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Appending_a_visible_block_while_scrolled"`
Expected: FAIL.

- [ ] **Step 3: Signal block appends from `Append`**

In `VirtualizedTranscriptView.Append` (`:115-123`), after computing the row delta:

```csharp
internal void Append(TranscriptBlock block)
{
    this.AppendCount++;
    var before = this.index.TotalRows;
    this.index.Append(block, this.currentWidth);
    var delta = this.index.TotalRows - before;
    this.viewport.OnRowsAppended(delta);
    if (delta > 0)
    {
        this.viewport.OnBlockAppended();   // whole visible block; hidden (0-row) blocks do not count
    }
    // ... existing SetNeedsDraw / scrolled event ...
}
```

Do NOT call `OnBlockAppended` from `ReplaceBlock`/`ReplaceLast` (streaming growth must not count).

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Appending_a_visible_block_while_scrolled"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs
git commit -m "feat(tui): count visible-block appends for the jump hint

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task C3: Floating jump-to-bottom hint

**Files:**
- Create: `src/Coda.Tui/Ui/Shells/JumpToBottomHint.cs`
- Modify: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs`
- Test: `tests/Coda.Tui.Tests/JumpToBottomHintTests.cs` (new), `FullscreenTuiShellTests.cs`

Context: today the header shows unseen text via `FullscreenTuiShell.UpdateHeader` (`:364-379`), refreshed by `RefreshHeaderForViewport` (`:379`) on `TranscriptScrolled`. Replace that text with a floating one-row hint anchored `Pos.AnchorEnd(1)` over the transcript, kept below `PromptOverlay`. Provide a pure `HintText` helper for singular/plural + jump/unseen wording; the hint is hidden while auto-following and is clickable → `TranscriptView.JumpToNewest()`.

- [ ] **Step 1: Write the failing test (pure text logic)**

```csharp
// tests/Coda.Tui.Tests/JumpToBottomHintTests.cs
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

public sealed class JumpToBottomHintTests
{
    [Fact]
    public void No_unseen_shows_jump_to_bottom()
        => Assert.Equal("Jump to bottom (Ctrl+End) ↓", JumpToBottomHint.HintText(0));

    [Fact]
    public void Singular_message()
        => Assert.Equal("1 new message (Ctrl+End) ↓", JumpToBottomHint.HintText(1));

    [Fact]
    public void Plural_messages()
        => Assert.Equal("2 new messages (Ctrl+End) ↓", JumpToBottomHint.HintText(2));
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~JumpToBottomHintTests"`
Expected: FAIL.

- [ ] **Step 3: Implement the hint view + pure text**

```csharp
// src/Coda.Tui/Ui/Shells/JumpToBottomHint.cs
using Coda.Tui.Ui.Rendering;
using Terminal.Gui;

namespace Coda.Tui.Ui.Shells;

/// <summary>A floating one-row hint anchored to the bottom of the transcript panel. Hidden while
/// auto-following; clickable to jump to the newest content.</summary>
internal sealed class JumpToBottomHint : View
{
    private readonly TuiTheme theme;
    private int unseenBlocks;

    public JumpToBottomHint(TuiTheme theme)
    {
        this.theme = theme;
        this.CanFocus = false;
        this.Height = 1;
        this.Visible = false;
    }

    /// <summary>Fired when the hint is clicked.</summary>
    public event Action? Jump;

    public static string HintText(int unseenBlocks) => unseenBlocks <= 0
        ? "Jump to bottom (Ctrl+End) ↓"
        : $"{unseenBlocks} new message{(unseenBlocks == 1 ? string.Empty : "s")} (Ctrl+End) ↓";

    /// <summary>Updates visibility + label. <paramref name="autoFollow"/> true hides the hint.</summary>
    public void Update(bool autoFollow, int unseenBlocks)
    {
        this.unseenBlocks = unseenBlocks;
        this.Visible = !autoFollow;
        this.SetNeedsDraw();
    }

    protected override bool OnMouseEvent(MouseEventArgs mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.Button1Clicked))
        {
            this.Jump?.Invoke();
            return true;
        }

        return false;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var text = HintText(this.unseenBlocks);
        var width = Math.Max(0, this.Viewport.Width);
        var display = text.Length > width ? text[..width] : text;
        var left = Math.Max(0, (width - display.Length) / 2);   // centered when space permits
        this.SetAttribute(this.theme.JumpHintAttribute);        // Warm Ember fg on contrasting dark bg
        this.Move(0, 0);
        this.AddStr(new string(' ', width));
        this.Move(left, 0);
        this.AddStr(display);
        return true;
    }
}
```

Add a `JumpHintAttribute` to `TuiTheme` (Warm Ember foreground on a contrasting dark background) mirroring the existing themed attributes.

- [ ] **Step 4: Wire the hint into the shell; drop the header unseen text**

In `FullscreenTuiShell.BuildLayout` (near `:105-110`), after building `this.transcript`:

```csharp
this.jumpHint = new JumpToBottomHint(this.Theme)
{
    X = 0,
    Y = Pos.AnchorEnd(1),          // floating at the bottom of the transcript panel
    Width = Dim.Fill(),
};
this.jumpHint.Jump += () =>
{
    this.TranscriptView.JumpToNewest();
    this.RefreshHeaderForViewport();
};
this.Add(this.jumpHint);            // added before PromptOverlay so the modal stays on top
```

Change `UpdateHeader` (`:364-373`) to stop appending the unseen text (header shows only `left`), and extend `RefreshHeaderForViewport` (`:379`) to also update the hint:

```csharp
private void RefreshHeaderForViewport()
{
    this.UpdateHeader(this.Snapshot);
    this.jumpHint.Update(this.transcript.AutoFollow, this.transcript.UnseenBlocks);
}
```

Unsubscribe/dispose in the shell's dispose path (near `:385`).

- [ ] **Step 5: Run GREEN + shell regression**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~JumpToBottomHintTests|FullyQualifiedName~FullscreenTuiShellTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Coda.Tui/Ui/Shells/JumpToBottomHint.cs src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs src/Coda.Tui/Ui/Rendering/TuiTheme.cs tests/Coda.Tui.Tests/JumpToBottomHintTests.cs tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs
git commit -m "feat(tui): floating jump-to-bottom hint with block count

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task C4: Shell-global Ctrl+End

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Test: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`

Context: the composer routes every key through `TryHandleShellKey` (`TerminalGuiShellBase.cs:117` wiring; method `:437-513`) which today claims Esc/Ctrl+C/Ctrl+B; transcript-focused Ctrl+End already works via `VirtualizedTranscriptView.OnKeyDown` (`:507-511`). Add Ctrl+End to `TryHandleShellKey` so it also fires while the composer holds focus, equivalent to the transcript path.

- [ ] **Step 1: Write the failing test**

```csharp
// FullscreenTuiShellTests.cs — add
[Fact]
public void Ctrl_end_jumps_to_bottom_from_composer_focus()
{
    using var fixture = RetainedShellFixture.Create();
    var view = fixture.Shell.Transcript;
    view.SetViewportHeightForTest(3);
    view.ReplaceAll(ManyBlocks(20));
    view.ScrollBy(-10);
    Assert.False(view.AutoFollow);

    fixture.Shell.Composer.SetFocus();
    fixture.Shell.Composer.NewKeyDownEvent(Key.End.WithCtrl);   // composer has focus

    Assert.True(view.AutoFollow);       // shell-global handler jumped to bottom
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Ctrl_end_jumps_to_bottom_from_composer_focus"`
Expected: FAIL.

- [ ] **Step 3: Claim Ctrl+End in `TryHandleShellKey`**

Add near the Esc/Ctrl+C branches (`:451/484`):

```csharp
if (key == Key.End.WithCtrl)
{
    this.TranscriptView.JumpToNewest();
    this.RefreshHeaderForViewport();
    return true;
}
```

(Because the transcript's own `HandleUnhandledShellKey` calls `TryHandleShellKey` first at `:414`, transcript-focused Ctrl+End remains equivalent.)

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Ctrl_end"`
Expected: PASS (including the existing transcript-focus `Key.End.WithCtrl` test).

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs
git commit -m "feat(tui): shell-global Ctrl+End jump-to-bottom

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task C5: Interactive right-side scrollbar

**Files:**
- Create: `src/Coda.Tui/Ui/Shells/ScrollbarMetrics.cs`
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs`
- Test: `tests/Coda.Tui.Tests/ScrollbarMetricsTests.cs` (new), `FullscreenTuiShellTests.cs`

Context: drawing is `OnDrawingContent` (`:253-276`); content width is taken in `SyncViewportMetrics` (`:561-570`) and `DrawRow` (`:288-360`, full-width fill `:299`, right annotation `:351`). Mouse is `ProcessMouse` (`:366-439`), gated by `mouseService.IsMouseDisabled` (`:371`). Reserve one right-edge cell when `ContentRows > ViewportHeight`; compute the thumb from a pure `ScrollbarMetrics`; hit-test in `ProcessMouse` (paging/drag/release), keeping the scrollbar visible-but-inert under `--no-mouse`.

- [ ] **Step 1: Write the failing test (pure thumb math)**

```csharp
// tests/Coda.Tui.Tests/ScrollbarMetricsTests.cs
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

public sealed class ScrollbarMetricsTests
{
    [Fact]
    public void All_content_fits_full_height_thumb()
    {
        var m = ScrollbarMetrics.Compute(contentRows: 5, viewportHeight: 10, topRow: 0);
        Assert.Equal(0, m.ThumbTop);
        Assert.Equal(10, m.ThumbHeight);          // full height
    }

    [Fact]
    public void At_top_thumb_at_top()
    {
        var m = ScrollbarMetrics.Compute(contentRows: 100, viewportHeight: 10, topRow: 0);
        Assert.Equal(0, m.ThumbTop);
        Assert.True(m.ThumbHeight >= 1);
    }

    [Fact]
    public void At_bottom_thumb_flush_bottom()
    {
        var m = ScrollbarMetrics.Compute(contentRows: 100, viewportHeight: 10, topRow: 90);
        Assert.Equal(10, m.ThumbTop + m.ThumbHeight);
    }

    [Fact]
    public void Minimum_thumb_is_one_row()
    {
        var m = ScrollbarMetrics.Compute(contentRows: 100000, viewportHeight: 10, topRow: 5);
        Assert.True(m.ThumbHeight >= 1);
    }

    [Fact]
    public void Clamps_for_empty_and_one_row_viewport()
    {
        var empty = ScrollbarMetrics.Compute(contentRows: 0, viewportHeight: 0, topRow: 0);
        Assert.Equal(0, empty.ThumbHeight);
        var one = ScrollbarMetrics.Compute(contentRows: 50, viewportHeight: 1, topRow: 25);
        Assert.Equal(1, one.ThumbHeight);
    }

    [Fact]
    public void Position_maps_pointer_to_top_row()
    {
        // Dragging the thumb to the middle of a 10-row track over 100 rows lands near topRow 45-55.
        var top = ScrollbarMetrics.TopRowForPointer(pointerY: 5, viewportHeight: 10, contentRows: 100);
        Assert.InRange(top, 40, 60);
    }
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ScrollbarMetricsTests"`
Expected: FAIL — type absent.

- [ ] **Step 3: Implement `ScrollbarMetrics`**

```csharp
// src/Coda.Tui/Ui/Shells/ScrollbarMetrics.cs
namespace Coda.Tui.Ui.Shells;

/// <summary>Pure thumb geometry for the transcript scrollbar. All inputs clamp defensively.</summary>
public readonly record struct ScrollbarMetrics(int ThumbTop, int ThumbHeight)
{
    public static ScrollbarMetrics Compute(int contentRows, int viewportHeight, int topRow)
    {
        if (viewportHeight <= 0 || contentRows <= 0)
        {
            return new ScrollbarMetrics(0, 0);
        }

        if (contentRows <= viewportHeight)
        {
            return new ScrollbarMetrics(0, viewportHeight);   // full-height thumb; everything fits
        }

        var thumbHeight = Math.Max(1, (int)Math.Round((double)viewportHeight * viewportHeight / contentRows));
        thumbHeight = Math.Min(thumbHeight, viewportHeight);

        var maxTop = contentRows - viewportHeight;
        var clampedTop = Math.Clamp(topRow, 0, maxTop);
        var maxThumbTop = viewportHeight - thumbHeight;
        var thumbTop = maxTop == 0 ? 0 : (int)Math.Round((double)clampedTop / maxTop * maxThumbTop);
        thumbTop = Math.Clamp(thumbTop, 0, maxThumbTop);
        return new ScrollbarMetrics(thumbTop, thumbHeight);
    }

    /// <summary>Maps a pointer Y within the track to a target TopRow (thumb drag).</summary>
    public static int TopRowForPointer(int pointerY, int viewportHeight, int contentRows)
    {
        if (viewportHeight <= 1 || contentRows <= viewportHeight)
        {
            return 0;
        }

        var maxTop = contentRows - viewportHeight;
        var fraction = Math.Clamp((double)pointerY / (viewportHeight - 1), 0.0, 1.0);
        return (int)Math.Round(fraction * maxTop);
    }
}
```

- [ ] **Step 4: Reserve the column, draw the track/thumb, and hit-test the mouse**

In `SyncViewportMetrics` (`:561-570`) and `DrawRow` (`:288`), subtract 1 from the usable content width when `this.index.TotalRows > this.Viewport.Height` (store a `scrollbarVisible` flag + `ContentWidth => Viewport.Width - (scrollbarVisible ? 1 : 0)`). Draw the track/thumb as a final pass in `OnDrawingContent` (`:253-276`):

```csharp
// end of OnDrawingContent, after drawing rows
if (this.scrollbarVisible)
{
    var col = this.Viewport.Width - 1;
    var m = ScrollbarMetrics.Compute(this.viewport.ContentRows, this.viewport.ViewportHeight, this.viewport.TopRow);
    for (var y = 0; y < this.viewport.ViewportHeight; y++)
    {
        var inThumb = y >= m.ThumbTop && y < m.ThumbTop + m.ThumbHeight;
        this.SetAttribute(inThumb ? this.theme.ScrollbarThumbAttribute : this.theme.ScrollbarTrackAttribute);
        this.Move(col, y);
        this.AddRune(inThumb ? (Rune)'█' : (Rune)'│');
    }
}
```

In `ProcessMouse` (`:366`), before the existing selection logic and still gated by `IsMouseDisabled` (`:371`, so `--no-mouse` shows-but-inert), branch when `mouse.Position.X == Viewport.Width - 1` and `scrollbarVisible`:

```csharp
if (this.scrollbarVisible && mouse.Position.X == this.Viewport.Width - 1)
{
    var m = ScrollbarMetrics.Compute(this.viewport.ContentRows, this.viewport.ViewportHeight, this.viewport.TopRow);
    if (mouse.Flags.HasFlag(MouseFlags.Button1Pressed))
    {
        if (mouse.Position.Y < m.ThumbTop) { this.ScrollBy(-this.viewport.ViewportHeight); return true; }   // page up
        if (mouse.Position.Y >= m.ThumbTop + m.ThumbHeight) { this.ScrollBy(this.viewport.ViewportHeight); return true; } // page down
        this.scrollbarDragging = true;
        this.app.Mouse?.GrabMouse(this);
        return true;
    }

    if (this.scrollbarDragging && mouse.Flags.HasFlag(MouseFlags.ReportMousePosition))
    {
        var target = ScrollbarMetrics.TopRowForPointer(mouse.Position.Y, this.viewport.ViewportHeight, this.viewport.ContentRows);
        this.viewport.ScrollToRow(target);   // add ScrollToRow(int): sets AutoFollow off + clamps TopRow
        this.SetNeedsDraw();
        this.TranscriptScrolled?.Invoke();
        return true;
    }

    if (mouse.Flags.HasFlag(MouseFlags.Button1Released))
    {
        this.scrollbarDragging = false;
        this.app.Mouse?.UngrabMouse();
        return true;
    }
}
```

Add `TranscriptViewportState.ScrollToRow(int row)` that sets `AutoFollow = false`, clamps `TopRow`, and — when the row reaches `MaxTopRow` — restores auto-follow and clears `UnseenBlocks`/`UnseenRows` (matches "reaching the bottom restores auto-follow").

- [ ] **Step 5: Write a view-level regression test**

```csharp
// FullscreenTuiShellTests.cs — add
[Fact]
public void Scrollbar_column_is_reserved_when_content_overflows()
{
    using var fixture = RetainedShellFixture.Create();
    var view = fixture.Shell.Transcript;
    view.SetViewportHeightForTest(3);
    view.ReplaceAll(ManyBlocks(50));
    Assert.True(view.ScrollbarVisibleForTest);           // expose the flag for the test
    // Wrapping/selection must not use the reserved last column:
    Assert.True(view.ContentWidthForTest < view.ActiveLayoutWidth + 1);
}
```

- [ ] **Step 6: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ScrollbarMetricsTests|FullyQualifiedName~Scrollbar_column_is_reserved"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Coda.Tui/Ui/Shells/ScrollbarMetrics.cs src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs src/Coda.Tui/Ui/Rendering/TuiTheme.cs tests/Coda.Tui.Tests/ScrollbarMetricsTests.cs tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs
git commit -m "feat(tui): interactive transcript scrollbar

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

# Group D — Windows Terminal input compatibility (parallel-safe)

Makes Shift+Enter insert a newline in Windows Terminal by preferring the Terminal.Gui ANSI input path (which negotiates the Kitty keyboard protocol in 2.4.17) and normalizing `CSI 13;2u` to `Key.Enter.WithShift`, below the composer. The existing action-map/composer tests (`ComposerControllerTests`, `ComposerViewTests`) remain regression coverage.

### Task D1: `TerminalInputCompatibility` — detection + driver selection + normalization

**Files:**
- Create: `src/Coda.Tui/Ui/Host/TerminalInputCompatibility.cs`
- Test: `tests/Coda.Tui.Tests/TerminalInputCompatibilityTests.cs` (new)

Context: no Windows Terminal detection exists anywhere (grep `WT_SESSION|WindowsTerminal|TERM_PROGRAM` → 0 hits). The ANSI driver name is `DriverRegistry.Names.ANSI` (used in `tests/Coda.Tui.Tests/RetainedShellFixture.cs:42`). Production passes `null` (platform default). This component is a pure, environment-driven decision + a pure key normalizer, so it is fully unit-testable without a live terminal.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Coda.Tui.Tests/TerminalInputCompatibilityTests.cs
using Coda.Tui.Ui.Host;
using Terminal.Gui;

namespace Coda.Tui.Tests;

public sealed class TerminalInputCompatibilityTests
{
    [Fact]
    public void Windows_terminal_prefers_ansi_driver()
    {
        var env = new Dictionary<string, string?> { ["WT_SESSION"] = "abc-123" };
        var driver = TerminalInputCompatibility.SelectDriverName(env.GetValueOrDefault);
        Assert.Equal(DriverRegistry.Names.ANSI, driver);
    }

    [Fact]
    public void Non_windows_terminal_keeps_default_driver()
    {
        var env = new Dictionary<string, string?>();
        var driver = TerminalInputCompatibility.SelectDriverName(env.GetValueOrDefault);
        Assert.Null(driver);      // null == Terminal.Gui platform default
    }

    [Fact]
    public void Csi_13_2u_normalizes_to_shift_enter()
    {
        // The Kitty encoding of Shift+Enter arrives as Key with the modified-Enter shape.
        var normalized = TerminalInputCompatibility.NormalizeModifiedEnter(Key.Enter.WithShift);
        Assert.Equal(Key.Enter.WithShift, normalized);   // native passes through unchanged
    }

    [Fact]
    public void Plain_enter_is_not_altered()
    {
        Assert.Equal(Key.Enter, TerminalInputCompatibility.NormalizeModifiedEnter(Key.Enter));
    }
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TerminalInputCompatibilityTests"`
Expected: FAIL — type absent.

- [ ] **Step 3: Implement the component**

```csharp
// src/Coda.Tui/Ui/Host/TerminalInputCompatibility.cs
using Terminal.Gui;

namespace Coda.Tui.Ui.Host;

/// <summary>
/// Chooses the Terminal.Gui input driver and normalizes modified-Enter for Windows Terminal so
/// Shift+Enter inserts a newline through the real decoder. Windows Terminal reports Shift+Enter via
/// the Kitty keyboard protocol (CSI 13;2u); the Terminal.Gui 2.4.17 ANSI driver can negotiate/parse
/// it. Outside supported Windows Terminal sessions the default driver is preserved. All decisions are
/// pure and bounded so capability detection can never crash or delay startup.
/// </summary>
internal static class TerminalInputCompatibility
{
    /// <summary>Returns the driver name to pass to <c>app.Init</c>, or null for the platform default.</summary>
    public static string? SelectDriverName(Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(getEnv);
        var wtSession = getEnv("WT_SESSION");
        return string.IsNullOrEmpty(wtSession) ? null : DriverRegistry.Names.ANSI;
    }

    /// <summary>Convenience overload using the process environment.</summary>
    public static string? SelectDriverName()
        => SelectDriverName(Environment.GetEnvironmentVariable);

    /// <summary>Normalizes a supported modified-Enter key to <c>Key.Enter.WithShift</c>; passes everything
    /// else through unchanged. Native <c>Key.Enter.WithShift</c> is returned as-is.</summary>
    public static Key NormalizeModifiedEnter(Key key)
    {
        if (key == Key.Enter.WithShift)
        {
            return Key.Enter.WithShift;
        }

        // The ANSI/Kitty decoder in 2.4.17 already surfaces CSI 13;2u as Enter+Shift; if a future
        // driver surfaces a distinct modified-Enter rune/keycode, map it here. Unknown keys pass through.
        return key;
    }
}
```

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TerminalInputCompatibilityTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Ui/Host/TerminalInputCompatibility.cs tests/Coda.Tui.Tests/TerminalInputCompatibilityTests.cs
git commit -m "feat(tui): Windows Terminal input compatibility component

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task D2: Apply the driver name in `TerminalGuiModeRunner`, guarded

**Files:**
- Modify: `src/Coda.Tui/Ui/Host/TerminalGuiModeRunner.cs`
- Modify: `src/Coda.Tui/InteractiveProgram.cs`
- Test: `tests/Coda.Tui.Tests/TerminalGuiModeRunnerTests.cs`

Context: the only Terminal.Gui init is `TerminalGuiModeRunner.RunTerminalGuiAsync` (`:120-128`): `app = applicationFactory(); ...; app.Init(this.driverName);`. `driverName` (field `:26`) is currently never set (runner constructed at `InteractiveProgram.cs:440-444` with no `driverName:` arg → null). Wire the detected driver name in, wrapped so detection failure falls back to `null`/default and never delays startup.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Coda.Tui.Tests/TerminalGuiModeRunnerTests.cs — add
[Fact]
public async Task Runner_uses_selected_driver_name_for_init()
{
    string? initDriver = "unset";
    var fakeApp = new FakeApplication(onInit: name => initDriver = name);   // existing/added fake
    var runner = new TerminalGuiModeRunner(
        shellFactory: _ => new FakeShell(),
        spectreSession: (_, _) => Task.CompletedTask,
        plainSession: (_, _) => Task.CompletedTask,
        mouseDisabled: false,
        driverName: "ansi",                       // NEW ctor arg
        applicationFactory: () => fakeApp);

    await runner.RunTerminalGuiAsync(TuiRunMode.Inline, /* ... */ CancellationToken.None);

    Assert.Equal("ansi", initDriver);
}
```

(Use the existing runner test harness/fakes in this file; if none, add a minimal `FakeApplication` implementing the `IApplication` seam the runner already depends on.)

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Runner_uses_selected_driver_name_for_init"`
Expected: FAIL — no `driverName` ctor arg / not honored.

- [ ] **Step 3: Thread the driver name through the runner and composition root**

`TerminalGuiModeRunner` already stores `driverName` (`:26`) and uses it at `app.Init(this.driverName)` (`:128`) — ensure the ctor accepts and assigns it (add the parameter if the existing ctor lacks it). In `InteractiveProgram.cs:440-444`, pass the detected name, guarded:

```csharp
string? driverName;
try
{
    driverName = TerminalInputCompatibility.SelectDriverName();
}
catch
{
    driverName = null;   // detection must never crash/delay startup
}

var modeRunner = new TerminalGuiModeRunner(
    ShellFactory,
    RunSpectreSessionAsync,
    RunPlainSessionAsync,
    mouseDisabled: options.MouseDisabled,
    driverName: driverName);
```

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TerminalGuiModeRunnerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Ui/Host/TerminalGuiModeRunner.cs src/Coda.Tui/InteractiveProgram.cs tests/Coda.Tui.Tests/TerminalGuiModeRunnerTests.cs
git commit -m "feat(tui): select ANSI driver for Windows Terminal at startup

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task D3: Decoder-level Shift+Enter → newline validation

**Files:**
- Modify: `tests/Coda.Tui.Tests/TerminalInputCompatibilityTests.cs`
- Test only (proves the normalized key drives the existing newline action)

Context: existing widget tests synthesize `Key.Enter.WithShift` and cover the action map (`ComposerControllerTests.cs:334`, `ComposerViewTests.cs:139`) but not the real decoder. Add a test that feeds the modified-Enter representation through `NormalizeModifiedEnter` and then through `UiActionMap.Map` to prove it becomes `InsertNewline`, and that Ctrl+Enter/Ctrl+J/plain Enter still behave. Keep the existing composer tests as regression coverage.

- [ ] **Step 1: Write the decoder-to-action test**

```csharp
// tests/Coda.Tui.Tests/TerminalInputCompatibilityTests.cs — add
using Coda.Tui.Ui.Input;

[Fact]
public void Normalized_shift_enter_maps_to_insert_newline()
{
    var context = new UiInputContext(ComposerEmpty: false, CompletionVisible: false, CanMoveVisualUp: true, CanMoveVisualDown: true);

    var shiftEnter = TerminalInputCompatibility.NormalizeModifiedEnter(Key.Enter.WithShift);
    Assert.Equal(UiAction.InsertNewline, UiActionMap.Map(shiftEnter, context));

    Assert.Equal(UiAction.InsertNewline, UiActionMap.Map(Key.Enter.WithCtrl, context));
    Assert.Equal(UiAction.InsertNewline, UiActionMap.Map(Key.J.WithCtrl, context));
    Assert.Equal(UiAction.Submit, UiActionMap.Map(Key.Enter, context));
}
```

- [ ] **Step 2: Run GREEN (no production change; the normalizer + map already exist)**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Normalized_shift_enter_maps_to_insert_newline"`
Expected: PASS. If it fails because the driver does not surface `Key.Enter.WithShift`, extend `NormalizeModifiedEnter` to map the actual keycode/rune the ANSI driver reports (verify against Terminal.Gui 2.4.17's `CSI 13;2u` parsing) — then re-run.

- [ ] **Step 3: Run the composer regression suite**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ComposerControllerTests|FullyQualifiedName~ComposerViewTests"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/Coda.Tui.Tests/TerminalInputCompatibilityTests.cs
git commit -m "test(tui): decoder-level Shift+Enter to newline validation

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

# Group E — TUI integration (depends on Group B seams)

Wires the shared steering queue into the TUI: busy submissions become visible pending messages, delivery converts them in place, and Up recalls them into the composer. Start this group after Groups A–D land the seams it consumes (`SteeringInbox`, `IAgentSink.OnSteeringDelivered`, `CodaSession.Steer`/`RecallSteering`).

### Task E1: Pending block, events, and reducer arms

**Files:**
- Modify: `src/Coda.Tui/Ui/State/TranscriptBlock.cs`
- Modify: `src/Coda.Tui/Ui/Events/UiEvent.cs`
- Modify: `src/Coda.Tui/Ui/State/UiReducer.cs`
- Test: `tests/Coda.Tui.Tests/UiReducerTests.cs`

Context: user submissions become `UserTranscriptBlock` via `UserPromptSubmittedEvent` reduced at `UiReducer.cs:23`; the `Append` helper is `:135-136`; in-place updates use `LastIndex` + `SetItem` (`ResolvePermission` `:282-297` is the closest template). A pending message needs its own block carrying the queue entry id (a string, distinct from the block `Guid`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Coda.Tui.Tests/UiReducerTests.cs — add
[Fact]
public void Enqueued_prompt_appends_a_pending_user_block()
{
    var state = UiReducer.Reduce(UiSessionSnapshot.Empty,
        new UserPromptEnqueuedEvent("q-1", "hello later", DateTimeOffset.UnixEpoch));

    var block = Assert.IsType<PendingUserTranscriptBlock>(Assert.Single(state.Transcript));
    Assert.Equal("q-1", block.QueueEntryId);
    Assert.Equal("hello later", block.Text);
}

[Fact]
public void Delivery_converts_pending_block_in_place_preserving_id()
{
    var s0 = UiReducer.Reduce(UiSessionSnapshot.Empty,
        new UserPromptEnqueuedEvent("q-1", "hello later", DateTimeOffset.UnixEpoch));
    var pendingId = s0.Transcript[0].Id;

    var s1 = UiReducer.Reduce(s0, new SteeringDeliveredEvent(new[] { "q-1" }));

    var block = Assert.IsType<UserTranscriptBlock>(Assert.Single(s1.Transcript));
    Assert.Equal(pendingId, block.Id);          // identity preserved
    Assert.Equal("hello later", block.Text);
}

[Fact]
public void Recall_removes_only_still_pending_blocks()
{
    var s0 = UiReducer.Reduce(UiSessionSnapshot.Empty,
        new UserPromptEnqueuedEvent("q-1", "a", DateTimeOffset.UnixEpoch));
    var s1 = UiReducer.Reduce(s0, new UserPromptEnqueuedEvent("q-2", "b", DateTimeOffset.UnixEpoch));

    var s2 = UiReducer.Reduce(s1, new PendingSteeringRecalledEvent(new[] { "q-1", "q-2" }));

    Assert.Empty(s2.Transcript);
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Enqueued_prompt_appends|FullyQualifiedName~Delivery_converts_pending|FullyQualifiedName~Recall_removes_only"`
Expected: FAIL — types absent.

- [ ] **Step 3: Add the pending block**

```csharp
// src/Coda.Tui/Ui/State/TranscriptBlock.cs — add
/// <summary>A submitted-while-busy user prompt awaiting delivery. Carries the shared-queue entry id so
/// delivery/recall can match it. Rendered with normal user styling plus a pending annotation.</summary>
public sealed record PendingUserTranscriptBlock(Guid Id, string QueueEntryId, string Text, DateTimeOffset EnqueuedAt)
    : TranscriptBlock(Id);
```

- [ ] **Step 4: Add the events**

```csharp
// src/Coda.Tui/Ui/Events/UiEvent.cs — add
/// <summary>A prompt accepted into the steering queue while a turn was busy.</summary>
public sealed record UserPromptEnqueuedEvent(string QueueEntryId, string Text, DateTimeOffset EnqueuedAt) : UiEvent;

/// <summary>Queue entries were delivered into the running turn; convert matching pending blocks in place.</summary>
public sealed record SteeringDeliveredEvent(IReadOnlyList<string> MessageIds) : UiEvent;

/// <summary>Pending entries were recalled into the composer; remove their pending blocks.</summary>
public sealed record PendingSteeringRecalledEvent(IReadOnlyList<string> MessageIds) : UiEvent;
```

- [ ] **Step 5: Add the reducer arms**

In `UiReducer.Reduce` add (near the user/permission arms):

```csharp
UserPromptEnqueuedEvent e => Append(state,
    new PendingUserTranscriptBlock(Guid.NewGuid(), e.QueueEntryId, e.Text, e.EnqueuedAt)),
SteeringDeliveredEvent e => DeliverPending(state, e.MessageIds),
PendingSteeringRecalledEvent e => RemovePending(state, e.MessageIds),
```

with helpers (mirror `ResolvePermission`/`LastIndex`):

```csharp
private static UiSessionSnapshot DeliverPending(UiSessionSnapshot state, IReadOnlyList<string> ids)
{
    var set = ids.ToHashSet(StringComparer.Ordinal);
    var transcript = state.Transcript;
    for (var i = 0; i < transcript.Length; i++)
    {
        if (transcript[i] is PendingUserTranscriptBlock p && set.Contains(p.QueueEntryId))
        {
            transcript = transcript.SetItem(i, new UserTranscriptBlock(p.Id, p.Text, p.EnqueuedAt));
        }
    }

    return state with { Transcript = transcript };
}

private static UiSessionSnapshot RemovePending(UiSessionSnapshot state, IReadOnlyList<string> ids)
{
    var set = ids.ToHashSet(StringComparer.Ordinal);
    return state with
    {
        Transcript = state.Transcript
            .Where(b => b is not PendingUserTranscriptBlock p || !set.Contains(p.QueueEntryId))
            .ToImmutableArray(),
    };
}
```

Also add a formatter branch for `PendingUserTranscriptBlock` in `TranscriptBlockFormatter.Format` (Task A5's `switch`): render as a user block plus a distinct pending annotation (e.g. right-text `"pending"`), so it shows immediately.

- [ ] **Step 6: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~UiReducerTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Coda.Tui/Ui/State/TranscriptBlock.cs src/Coda.Tui/Ui/Events/UiEvent.cs src/Coda.Tui/Ui/State/UiReducer.cs src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs tests/Coda.Tui.Tests/UiReducerTests.cs
git commit -m "feat(tui): pending user block + enqueue/deliver/recall reducer arms

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task E2: AgentRunner pass-throughs + TuiAgentSink delivery event

**Files:**
- Modify: `src/Coda.Tui/Agent/AgentRunner.cs`
- Modify: `src/Coda.Tui/Agent/TuiAgentSink.cs`
- Test: `tests/Coda.Tui.Tests/AgentRunnerSteeringTests.cs` (new), `tests/Coda.Tui.Tests/TuiAgentSinkTests.cs`

Context: `AgentRunner` wraps a private `CodaSession` (`AgentRunner.cs:34`) and exposes `HasActiveTurn` (`:57`) but no steering. `TuiAgentSink` forwards lifecycle events by publishing `UiEvent`s (e.g. `OnLimitReached` publishes `LimitReachedEvent`). Add `Steer`/`RecallSteering` pass-throughs and make `TuiAgentSink.OnSteeringDelivered` publish the `SteeringDeliveredEvent`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Coda.Tui.Tests/TuiAgentSinkTests.cs — add
[Fact]
public void OnSteeringDelivered_publishes_delivery_event()
{
    var events = new RecordingUiEvents();
    var sink = new TuiAgentSink(events);
    ((IAgentSink)sink).OnSteeringDelivered(new[] { "q-1", "q-2" });

    var published = Assert.IsType<SteeringDeliveredEvent>(Assert.Single(events.Events));
    Assert.Equal(new[] { "q-1", "q-2" }, published.MessageIds);
}
```

```csharp
// tests/Coda.Tui.Tests/AgentRunnerSteeringTests.cs
[Fact]
public void Steer_and_recall_round_trip_through_the_session()
{
    using var runner = /* build AgentRunner with an initialized session via existing helper */;
    var id = runner.Steer("do X");
    Assert.False(string.IsNullOrWhiteSpace(id));
    var recalled = runner.RecallSteering();
    Assert.Single(recalled);
    Assert.Equal("do X", recalled[0].Text);
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiAgentSinkTests|FullyQualifiedName~AgentRunnerSteeringTests"`
Expected: FAIL.

- [ ] **Step 3: Add the pass-throughs + sink publish**

`AgentRunner.cs`:

```csharp
/// <summary>Queues a steering message for the running turn; returns the accepted queue entry id, or null.</summary>
public string? Steer(string text) => this.session?.Steer(text);

/// <summary>Atomically recalls all still-pending steering entries.</summary>
public IReadOnlyList<SteeringEntry> RecallSteering() => this.session?.RecallSteering() ?? [];
```

`TuiAgentSink.cs` (mirror the `OnLimitReached` publisher):

```csharp
public void OnSteeringDelivered(IReadOnlyList<string> messageIds)
    => this.publisher.Publish(new SteeringDeliveredEvent(messageIds));
```

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiAgentSinkTests|FullyQualifiedName~AgentRunnerSteeringTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Agent/AgentRunner.cs src/Coda.Tui/Agent/TuiAgentSink.cs tests/Coda.Tui.Tests/TuiAgentSinkTests.cs tests/Coda.Tui.Tests/AgentRunnerSteeringTests.cs
git commit -m "feat(tui): AgentRunner steering pass-throughs; sink publishes delivery

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task E3: Busy submission enqueues instead of dropping

**Files:**
- Modify: `src/Coda.Tui/Ui/TuiController.cs`
- Test: `tests/Coda.Tui.Tests/TuiControllerTests.cs`

Context: `OnSubmitted` (`TuiController.cs:303-336`) drops ordinary busy submissions at `:328` (`return;`) after routing only live permission commands to the side-band chain. Change the busy ordinary path to enqueue into the session queue (via `AgentRunner.Steer`), publish `UserPromptEnqueuedEvent` with the returned id, and — when the queue is sealed (`Steer` returns null, i.e. the close race is lost) — retain the text and dispatch it as a fresh prompt when the dispatcher releases. The controller needs a `steer` delegate wired to `runner.Steer` (add alongside `dispatch`/`hasActiveTurn` in the production ctor and a test-seam ctor).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Coda.Tui.Tests/TuiControllerTests.cs — add
[Fact]
public async Task Busy_ordinary_submission_is_enqueued_and_publishes_pending_event()
{
    var release = new TaskCompletionSource();
    var started = new TaskCompletionSource();
    var events = new RecordingUiEvents();
    var steered = new List<string>();

    var controller = new TuiController(
        dispatch: async (_, _) => { started.TrySetResult(); await release.Task; return CommandResult.Continue; },
        tryInterrupt: () => false,
        steer: text => { steered.Add(text); return "q-1"; },     // NEW seam: returns accepted id
        publisher: events,
        initialSnapshot: UiSessionSnapshot.Empty);

    controller.OnSubmitted("run the agent");
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

    controller.OnSubmitted("do this too");                       // busy -> enqueue

    Assert.Contains("do this too", steered);
    Assert.Contains(events.Events, e => e is UserPromptEnqueuedEvent { QueueEntryId: "q-1", Text: "do this too" });

    release.SetResult();
    if (controller.CurrentDispatch is { } d) { await d.WaitAsync(TimeSpan.FromSeconds(5)); }
}

[Fact]
public async Task Busy_submission_that_loses_the_seal_race_is_retained_as_next_prompt()
{
    var release = new TaskCompletionSource();
    var dispatched = new System.Collections.Concurrent.ConcurrentQueue<string>();

    var controller = new TuiController(
        dispatch: async (text, _) =>
        {
            dispatched.Enqueue(text);
            if (dispatched.Count == 1) { await release.Task; }   // hold the first turn open
            return CommandResult.Continue;
        },
        tryInterrupt: () => false,
        steer: _ => null,                                        // sealed -> not accepted
        publisher: new RecordingUiEvents(),
        initialSnapshot: UiSessionSnapshot.Empty);

    controller.OnSubmitted("first");
    // wait until first is dispatched, then submit while busy with a sealed queue
    await WaitUntil(() => dispatched.Contains("first"));
    controller.OnSubmitted("second");                            // retained as next prompt
    release.SetResult();

    await WaitUntil(() => dispatched.Contains("second"));
    Assert.Contains("second", dispatched);
}
```

(Use the file's existing wait helpers/`CurrentDispatch` seam; add a tiny `WaitUntil` local if none exists.)

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Busy_ordinary_submission_is_enqueued|FullyQualifiedName~Busy_submission_that_loses_the_seal_race"`
Expected: FAIL — no `steer` seam; busy path still drops.

- [ ] **Step 3: Add the `steer` seam and rewrite the busy ordinary path**

Add a `private readonly Func<string, string?> steer;` field, plumb it through the production ctor (`steer: runner.Steer`) and a test-seam ctor. Replace the busy branch (`TuiController.cs:319-329`):

```csharp
if (this.dispatchInFlight)
{
    // Busy: safe live permission commands still run out-of-band.
    if (LivePermissionCommands.IsLivePermissionCommand(CommandParser.Parse(text)))
    {
        this.QueueSidebandCommand(text);
        return;
    }

    // Ordinary submission while busy: queue it as steering so it is delivered at the next safe
    // boundary and is visible + recallable. If the queue is already sealed (we lost the close
    // race at turn end), retain the text and dispatch it as the next normal prompt.
    var acceptedId = this.steer(text);
    if (acceptedId is not null)
    {
        this.publisher.Publish(new UserPromptEnqueuedEvent(acceptedId, text, DateTimeOffset.Now));
    }
    else
    {
        this.retainedNextPrompt = text;   // dispatched when the dispatcher releases (see RunDispatchAsync)
    }

    return;
}
```

In `RunDispatchAsync`'s `finally` (`:435-443`), after clearing `dispatchInFlight`, drain any retained prompt by re-invoking `OnSubmitted`:

```csharp
finally
{
    string? retained;
    lock (this.gate)
    {
        this.dispatchInFlight = false;
        this.dispatchCts?.Dispose();
        this.dispatchCts = null;
        this.dispatchTask = null;
        retained = this.retainedNextPrompt;
        this.retainedNextPrompt = null;
    }

    if (retained is not null && !Volatile.Read(ref this.exitRequested))
    {
        this.OnSubmitted(retained);   // now dispatched as a fresh prompt
    }
}
```

- [ ] **Step 4: Run GREEN + controller regression**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiControllerTests"`
Expected: PASS (the existing "busy rejects ordinary submission" test must be updated: ordinary busy submissions now enqueue; assert the enqueue seam was called instead of asserting a drop).

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Ui/TuiController.cs tests/Coda.Tui.Tests/TuiControllerTests.cs
git commit -m "feat(tui): busy submissions enqueue as steering; retain on seal race

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task E4: Up recalls queued messages into the composer

**Files:**
- Modify: `src/Coda.Tui/Ui/Input/UiActionMap.cs`, `src/Coda.Tui/Ui/Input/UiAction.cs`
- Modify: `src/Coda.Tui/Ui/Input/ComposerView.cs`, `src/Coda.Tui/Ui/Input/ComposerController.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs` (wire recall action to the controller/runner)
- Test: `tests/Coda.Tui.Tests/ComposerControllerTests.cs`, `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`

Context: the Up precedence lives in `UiActionMap.Map` `Key.CursorUp` branch (`:66-73`): `CompletionVisible ? CompletionPrevious : CanMoveVisualUp ? CursorVisualUp : HistoryPrevious`. `UiInputContext` already carries `ComposerEmpty`, `CompletionVisible`, `CanMoveVisualUp` (`:10-14`). Modal prompts are excluded automatically because `PromptOverlay` consumes keys at a higher layer (`PromptOverlay.cs:97-120`). Recall must fire only when there is ≥1 pending message AND the composer is empty AND the caret is on the first visual line AND completion is not visible. Add a `HasPendingQueued` flag to the context and a new `RecallQueued` action that yields to `HistoryPrevious` when there is nothing pending.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Coda.Tui.Tests/ComposerControllerTests.cs — add
[Fact]
public void Up_recalls_queued_when_empty_first_line_and_pending()
{
    var ctx = new UiInputContext(
        ComposerEmpty: true, CompletionVisible: false, CanMoveVisualUp: false, CanMoveVisualDown: false)
    { HasPendingQueued = true };
    Assert.Equal(UiAction.RecallQueued, UiActionMap.Map(Key.CursorUp, ctx));
}

[Fact]
public void Up_falls_back_to_history_when_nothing_pending()
{
    var ctx = new UiInputContext(
        ComposerEmpty: true, CompletionVisible: false, CanMoveVisualUp: false, CanMoveVisualDown: false)
    { HasPendingQueued = false };
    Assert.Equal(UiAction.HistoryPrevious, UiActionMap.Map(Key.CursorUp, ctx));
}

[Fact]
public void Up_yields_to_completion_and_multiline_caret()
{
    var completion = new UiInputContext(true, CompletionVisible: true, false, false) { HasPendingQueued = true };
    Assert.Equal(UiAction.CompletionPrevious, UiActionMap.Map(Key.CursorUp, completion));

    var midCaret = new UiInputContext(false, false, CanMoveVisualUp: true, false) { HasPendingQueued = true };
    Assert.Equal(UiAction.CursorVisualUp, UiActionMap.Map(Key.CursorUp, midCaret));
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Up_recalls_queued|FullyQualifiedName~Up_falls_back_to_history|FullyQualifiedName~Up_yields_to_completion"`
Expected: FAIL — `RecallQueued`/`HasPendingQueued` absent.

- [ ] **Step 3: Extend the action map**

Add `RecallQueued` to `UiAction`. Add `public bool HasPendingQueued { get; init; }` to `UiInputContext`. Update the `Key.CursorUp` branch (`:66-73`):

```csharp
if (key == Key.CursorUp)
{
    if (context.CompletionVisible)
    {
        return UiAction.CompletionPrevious;
    }

    if (context.CanMoveVisualUp)
    {
        return UiAction.CursorVisualUp;
    }

    // First visual line, empty composer, and something queued -> recall queued messages.
    if (context.ComposerEmpty && context.HasPendingQueued)
    {
        return UiAction.RecallQueued;
    }

    return UiAction.HistoryPrevious;
}
```

- [ ] **Step 4: Build the context flag + execute the recall**

Where `HandleKeyDown` builds the `UiInputContext` (`ComposerView.cs:557-561`), set `HasPendingQueued` from a shell-provided predicate (e.g. `this.HasPendingQueued?.Invoke() ?? false`, wired in `TerminalGuiShellBase` to `controller.HasPendingQueued`). Handle the new action in `ComposerView.HandleAction` (near the `HistoryPrevious` case `:861`):

```csharp
case UiAction.RecallQueued:
    var recalled = this.RecallQueued?.Invoke();   // Func<string?> wired to the controller
    if (!string.IsNullOrEmpty(recalled))
    {
        this.SetDraft(recalled, recalled.Length);  // whole-draft swap; caret at end
    }

    return true;
```

The controller's `RecallQueued()` (add it): calls `runner.RecallSteering()`, publishes `PendingSteeringRecalledEvent(ids)` (removes the pending blocks), joins entry texts with a blank line, and returns the joined string:

```csharp
public string? RecallQueued()
{
    var entries = this.recall();     // Func<IReadOnlyList<SteeringEntry>> wired to runner.RecallSteering
    if (entries.Count == 0)
    {
        return null;
    }

    this.publisher.Publish(new PendingSteeringRecalledEvent(entries.Select(e => e.Id).ToArray()));
    return string.Join("\n\n", entries.Select(e => e.Text));
}
```

- [ ] **Step 5: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ComposerControllerTests|FullyQualifiedName~Up_recalls"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Coda.Tui/Ui/Input/UiActionMap.cs src/Coda.Tui/Ui/Input/UiAction.cs src/Coda.Tui/Ui/Input/ComposerView.cs src/Coda.Tui/Ui/Input/ComposerController.cs src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs src/Coda.Tui/Ui/TuiController.cs tests/Coda.Tui.Tests/ComposerControllerTests.cs
git commit -m "feat(tui): Up recalls queued messages into the composer

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task E5: Lifecycle — clear only still-pending rows on interrupt/teardown

**Files:**
- Modify: `src/Coda.Tui/Ui/TuiController.cs` (interrupt path), `src/Coda.Tui/Agent/AgentRunner.cs` (turn interrupt)
- Test: `tests/Coda.Tui.Tests/TuiControllerTests.cs`, `tests/Coda.Tui.Tests/UiReducerTests.cs`

Context: on turn interruption / session teardown, still-pending entries must be cleared (queue `Clear()` reopens/empties) and their pending blocks removed, while already-delivered user blocks remain. Delivered blocks are ordinary `UserTranscriptBlock`s already; only `PendingUserTranscriptBlock`s are removable. Mode switches preserve pending state because it lives in the session queue + snapshot, not the shell.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Coda.Tui.Tests/UiReducerTests.cs — add
[Fact]
public void Interrupt_removes_pending_but_keeps_delivered_blocks()
{
    var s0 = UiReducer.Reduce(UiSessionSnapshot.Empty, new UserPromptSubmittedEvent("delivered"));
    var s1 = UiReducer.Reduce(s0, new UserPromptEnqueuedEvent("q-1", "still pending", DateTimeOffset.UnixEpoch));

    var s2 = UiReducer.Reduce(s1, new PendingSteeringRecalledEvent(new[] { "q-1" }));   // teardown reuses recall-removal

    Assert.Single(s2.Transcript);
    Assert.IsType<UserTranscriptBlock>(s2.Transcript[0]);
}
```

- [ ] **Step 2: Run RED / confirm**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Interrupt_removes_pending_but_keeps_delivered"`
Expected: PASS if E1 landed the removal arm; otherwise implement it there first.

- [ ] **Step 3: Wire interrupt/teardown to clear pending**

In the controller's interrupt path (where `tryInterrupt` runs) and in `AgentRunner` turn-interrupt/dispose, call `runner.RecallSteering()` (drains + clears the queue) and publish `PendingSteeringRecalledEvent(ids)` so the pending blocks are removed. Do not touch delivered `UserTranscriptBlock`s.

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiControllerTests|FullyQualifiedName~UiReducerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Ui/TuiController.cs src/Coda.Tui/Agent/AgentRunner.cs tests/Coda.Tui.Tests/TuiControllerTests.cs tests/Coda.Tui.Tests/UiReducerTests.cs
git commit -m "feat(tui): clear only still-pending queued rows on interrupt/teardown

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task E6: Documentation

**Files:**
- Modify: `README.md`
- Modify: `src/Coda.Tui/ImmediateCli.cs` (interactive help text)
- Test: `tests/Coda.Tui.Tests/ImmediateCliTests.cs` (help-lists-controls test)

- [ ] **Step 1: Update the help test**

Extend the existing "help lists interactive controls" test to require the new affordances: `Ctrl+End` (jump to bottom), `Up` (recall queued), and `Shift+Enter` (newline). Add a README section documenting `toolDisplayMode` (`~/.coda/settings.json`, values verbose/compact/tiny, default tiny, user-only, takes effect next launch) and queued-message behavior.

- [ ] **Step 2: Run RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ImmediateCliTests"`
Expected: FAIL until help text is updated.

- [ ] **Step 3: Update help text + README**

Add the control lines to `ImmediateCli` help output and the `toolDisplayMode` + queued-message + Ctrl+End/scrollbar sections to `README.md`.

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ImmediateCliTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add README.md src/Coda.Tui/ImmediateCli.cs tests/Coda.Tui.Tests/ImmediateCliTests.cs
git commit -m "docs: document toolDisplayMode, queued messages, and new shortcuts

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

# Group F — Integration verification and single holistic review

Runs only after Groups A–E are integrated. Per the approved workflow there is NO per-task/per-group review; this group performs full-suite verification, then exactly one holistic review with the strongest available model.

### Task F1: Full build + full test suites

**Files:** none (verification only).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build LlmAuth.slnx -c Debug`
Expected: build succeeds with no errors.

- [ ] **Step 2: Run the engine suite**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj`
Expected: all pass. Watch for the known pre-existing flaky `Teams.SendMessageToolShutdownSelfStopTests.Runner_task_completes_within_5s` (timing; passes in isolation) — re-run that class alone if it flaps: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~Runner_task_completes_within_5s"`.

- [ ] **Step 3: Run the TUI suite**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj`
Expected: all pass.

- [ ] **Step 4: Targeted acceptance sweep (maps to spec §Acceptance criteria)**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptLayoutIndexSeparatorTests|FullyQualifiedName~TranscriptBlockFormatterTests|FullyQualifiedName~OperationalStatusProjectorTests|FullyQualifiedName~JumpToBottomHintTests|FullyQualifiedName~ScrollbarMetricsTests|FullyQualifiedName~TerminalInputCompatibilityTests|FullyQualifiedName~UiReducerTests|FullyQualifiedName~TuiControllerTests|FullyQualifiedName~ComposerControllerTests"`
Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SteeringInboxTests|FullyQualifiedName~AgentLoopTests|FullyQualifiedName~Serve|FullyQualifiedName~Task_recall|FullyQualifiedName~SettingsDefaultsTests"`
Expected: all pass.

- [ ] **Step 5: Manual smoke (data-preservation guard, spec §Data preservation / criterion 12)**

Confirm that with `toolDisplayMode: "tiny"`, serve JSON-RPC tool events (`event/toolCall`, `event/toolResult`), persisted history, and telemetry are still complete (inspect a serve session and a session bundle). Tool events must remain in `UiSessionSnapshot.Transcript`; only interactive rendering is affected.

- [ ] **Step 6: Commit any test adjustments**

```bash
git add -A
git commit -m "test: integration verification adjustments for dialog/queue/transcript UX

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task F2: One holistic strongest-model review

- [ ] **Step 1:** Dispatch a single code review with the strongest available model over the entire integrated diff (all of Groups A–E). Provide the spec (`docs/superpowers/specs/2026-07-22-dialog-queue-and-transcript-ux-design.md`) and this plan as references. Focus areas: the delivery/recall race in `SteeringInbox`; provider `tool_use`/`tool_result` validity for skipped tools; that tiny mode never suppresses engine/serve/history/telemetry data; scrollbar/hint mouse-capture release on dispose and mode-switch; Ctrl+End equivalence across focus.
- [ ] **Step 2:** Address the single review's findings, re-run Task F1, and record one final verdict. No further review cycles.

---

## Self-review (author checklist — completed while drafting)

**Spec coverage** — every numbered spec section maps to at least one task:
- §1 Transcript block spacing → A1, A2.
- §2 Tool display policy (setting, modes, tiny status, data preservation) → A3, A4, A5, A6, A7, A8; F1-Step 5.
- §3 Shared queued-steering model (queue primitive, safe boundaries, tool-use validity, delivery notification) → B1, B2, B3, B4.
- §4 TUI queued-message behavior (submit-while-busy, delivery, Up recall, lifecycle) → E3, E4, E5; reducer E1.
- §5 Serve and task APIs (steer messageId, recallSteering, steeringDelivered, task_recall) → B6, B7, B8, B9.
- §6 Shift+Enter in Windows Terminal → D1, D2, D3.
- §7 Transcript navigation chrome (hint, global Ctrl+End, scrollbar) → C1, C2, C3, C4, C5.
- §8 Error handling and lifecycle → covered across B1 (race), B4 (skip validity), A5/A8 (no source mutation), D2 (startup guard), C5 (capture release), E5 (pending clear).
- §9 Testing → every task ships tests; F1 aggregates.
- §10 Implementation organization / models / single review → Workflow section + Groups A–F.

**Placeholder scan:** no `TBD`/`implement later`/"add error handling" — every code-changing step shows real C#. Where an existing large-file body is not reproduced verbatim (e.g. `ToggleExpansionAt`, `SyncViewportMetrics`), the exact method + line anchor + the concrete inserted code + a pinning test are given.

**Type consistency:** `SteeringEntry(Id:string, Text, EnqueuedAt)` used everywhere; TUI `PendingUserTranscriptBlock` carries a separate `QueueEntryId:string` (block `Id` stays `Guid`); `IAgentSink.OnSteeringDelivered(IReadOnlyList<string>)`, `SteeringDeliveredEvent.MessageIds`, serve `event/steeringDelivered` all use string ids; `ToolDisplayMode {Verbose,Compact,Tiny}` used by formatter, plain renderer, and status projector; `SteerResult(Ok, MessageId?)` preserves `ok`.

**Known cross-group coordination points (integrate carefully in Group E):** `TranscriptBlockFormatter.Format` gains both the tool-mode overload (A5) and a `PendingUserTranscriptBlock` case (E1); `InteractiveProgram` composition root is touched by A8 (mode) and D2 (driver name); `TerminalGuiShellBase` is touched by C4 (Ctrl+End) and E4 (recall predicate wiring); `UiReducer`/`UiEvent`/`TranscriptBlock` gain the pending/delivery/recall members in E1. These are additive and do not conflict when merged in the A→B→C→D→E order.

---

## Execution handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-22-dialog-queue-and-transcript-ux.md`. Two execution options:

1. **Subagent-Driven (recommended)** — dispatch a fresh implementation agent (GPT-5.6 Luna / Terra) per task, running Groups A–D in parallel, then Group E, then Group F. Per the approved workflow, do NOT review between tasks; run one holistic strongest-model review only after all groups integrate (Task F2).
2. **Inline Execution** — execute tasks in this session using superpowers:executing-plans with checkpoints, still deferring all review to the single Task F2 pass.

Which approach?






