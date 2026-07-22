# Transcript Navigation Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden transcript following, detached anchoring, unread counting, jump control placement/hit-testing, global navigation, scrollbar lifecycle, and timestamp clearance in both Terminal.Gui shell modes.

**Architecture:** `TranscriptViewportState` remains the single source of truth for Following/Detached state, unseen counts, and a stable `(blockId, wrappedRowOffset)` detached anchor. `TranscriptLayoutIndex` translates anchors to current global rows after streaming replacement or reflow, while `VirtualizedTranscriptView` owns input routing, scrollbar capture, and mutation-time anchor restoration; both shells inherit the same layout and navigation behavior from the retained shell base.

**Tech Stack:** C# 14, .NET 10, Terminal.Gui 2.x, immutable transcript blocks, xUnit, existing retained transcript/layout index.

---

## File responsibility map

### New production file

- `src/Coda.Tui/Ui/Shells/TranscriptViewportAnchor.cs` — typed detached anchor shared by viewport state and layout index.

### Existing production files

- `src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs` — Following/Detached transitions, stable anchor ownership, and atomic unseen clearing.
- `src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs` — map global rows to typed anchors and anchors back to current rows after block-size or width changes.
- `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs` — preserve anchors across append/replace/reflow, route every navigation input, count only first visible insertions, and release mouse capture reliably.
- `src/Coda.Tui/Ui/Shells/JumpToBottomHint.cs` — approved labels, exact rendered hit target, and cell-aware centering.
- `src/Coda.Tui/Ui/Shells/ScrollbarMetrics.cs` — pure track/thumb/pointer geometry retained as the single scrollbar math authority.
- `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs` — shell-global `Ctrl+End` that yields to visible modal overlays.
- `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs` — dedicated navigation chrome row, shared by fullscreen and inherited inline layout.
- `src/Coda.Tui/Ui/Shells/InlineTuiShell.cs` — parity assertions only unless constructor/layout delegation needs adjustment.
- `src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs` — reserve a timestamp-to-scrollbar trailing gap.

### Test files

- `tests/Coda.Tui.Tests/TranscriptLayoutAnchorTests.cs` — anchor mapping and clamping.
- `tests/Coda.Tui.Tests/TranscriptViewportNavigationTests.cs` — pure Following/Detached and unseen transitions.
- `tests/Coda.Tui.Tests/TranscriptNavigationChromeTests.cs` — shell placement, click hit-testing, global jump, modal isolation, scrollbar behavior, and capture teardown.
- `tests/Coda.Tui.Tests/JumpToBottomHintTests.cs` — exact labels and cell-aware hit rectangle.
- `tests/Coda.Tui.Tests/ScrollbarMetricsTests.cs` — pure geometry regressions.
- `tests/Coda.Tui.Tests/TranscriptBlockFormatterTests.cs` — timestamp spacing at normal and narrow widths.
- `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs` and `tests/Coda.Tui.Tests/InlineTuiShellTests.cs` — retained-shell parity and streaming/reflow behavior.

## Invariants used by every task

- The public user-facing modes are exactly `Following` and `Detached`.
- Detached position is identified by transcript block ID plus wrapped-row offset, not only a global row number.
- Replacing or reflowing blocks never increments unseen count.
- The first visible insertion of a new top-level block increments unseen once while detached; hidden and separator-only rows do not.
- Reaching the bottom clears unseen state and restores Following in one state transition.
- A visible modal overlay owns `Ctrl+End`; the transcript behind it does not move.
- The jump control occupies its own layout row and accepts clicks only inside the text it actually rendered.
- Mouse capture is released on button-up, view disposal, shell teardown, and mode switch.

### Task 1: Add typed anchor mapping to the transcript layout index

**Files:**
- Create: `src/Coda.Tui/Ui/Shells/TranscriptViewportAnchor.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs`
- Create: `tests/Coda.Tui.Tests/TranscriptLayoutAnchorTests.cs`

- [ ] **Step 1: Write failing anchor mapping tests**

```csharp
using System.Collections.Immutable;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class TranscriptLayoutAnchorTests
{
    [Fact]
    public void ResolveAnchor_tracks_a_block_after_rows_before_it_expand()
    {
        var first = new CommandOutputTranscriptBlock(Guid.NewGuid(), "a");
        var anchored = new CommandOutputTranscriptBlock(Guid.NewGuid(), "b1|b2|b3");
        var last = new CommandOutputTranscriptBlock(Guid.NewGuid(), "c");
        var index = NewIndex();
        index.ReplaceAll([first, anchored, last], width: 80);
        var anchor = Assert.IsType<TranscriptViewportAnchor>(index.AnchorAt(globalRow: 3));
        Assert.Equal(new TranscriptViewportAnchor(anchored.Id, 1), anchor);

        index.ReplaceAt(
            0,
            new CommandOutputTranscriptBlock(first.Id, "a1|a2|a3|a4"),
            width: 80);

        Assert.Equal(6, index.ResolveAnchor(anchor));
    }

    [Fact]
    public void ResolveAnchor_clamps_when_the_anchored_block_shrinks()
    {
        var block = new CommandOutputTranscriptBlock(Guid.NewGuid(), "one|two|three");
        var index = NewIndex();
        index.ReplaceAll([block], width: 80);
        var anchor = new TranscriptViewportAnchor(block.Id, WrappedRowOffset: 2);

        index.ReplaceAt(0, block with { Text = "one" }, width: 80);

        Assert.Equal(1, index.ResolveAnchor(anchor));
    }

    [Fact]
    public void ResolveAnchor_returns_null_after_the_block_disappears()
    {
        var block = new CommandOutputTranscriptBlock(Guid.NewGuid(), "one");
        var index = NewIndex();
        index.ReplaceAll([block], width: 80);

        index.ReplaceAll(ImmutableArray<TranscriptBlock>.Empty, width: 80);

        Assert.Null(index.ResolveAnchor(new TranscriptViewportAnchor(block.Id, 0)));
    }

    private static TranscriptLayoutIndex NewIndex() =>
        new((block, width) =>
        {
            var text = ((CommandOutputTranscriptBlock)block).Text;
            return text.Split('|')
                .Select(value => new TranscriptRenderLine(value, TranscriptRole.Code))
                .ToArray();
        });
}
```

- [ ] **Step 2: Run the anchor tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptLayoutAnchorTests"`

Expected: FAIL because `TranscriptViewportAnchor`, `AnchorAt`, and `ResolveAnchor` do not exist.

- [ ] **Step 3: Implement anchor lookup and current-row resolution**

```csharp
namespace Coda.Tui.Ui.Shells;

internal readonly record struct TranscriptViewportAnchor(
    Guid BlockId,
    int WrappedRowOffset);
```

Maintain a `Dictionary<Guid, int> blockPositions` in `TranscriptLayoutIndex`; rebuild it in `ReplaceAll`, append the new position in `Append`, and update the replaced ID in `ReplaceAt`.

```csharp
public TranscriptViewportAnchor? AnchorAt(int globalRow)
{
    if (globalRow < 0 || globalRow >= this.TotalRows)
    {
        return null;
    }

    var blockIndex = this.FindBlock(globalRow);
    return new TranscriptViewportAnchor(
        this.blocks[blockIndex].Id,
        globalRow - this.prefix[blockIndex]);
}

public int? ResolveAnchor(TranscriptViewportAnchor anchor)
{
    if (!this.blockPositions.TryGetValue(anchor.BlockId, out var blockIndex))
    {
        return null;
    }

    var local = Math.Clamp(
        anchor.WrappedRowOffset,
        0,
        Math.Max(0, this.rowCounts[blockIndex] - 1));
    return this.prefix[blockIndex] + local;
}
```

Assert block IDs are unique when rebuilding the index; duplicate IDs indicate a reducer bug and should throw `InvalidOperationException` rather than silently resolving to the wrong block.

- [ ] **Step 4: Run the anchor tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptLayoutAnchorTests|FullyQualifiedName~TranscriptLayoutIndexTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\TranscriptViewportAnchor.cs src\Coda.Tui\Ui\Shells\TranscriptLayoutIndex.cs tests\Coda.Tui.Tests\TranscriptLayoutAnchorTests.cs
git commit -m "feat(tui): map stable transcript viewport anchors" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 2: Make Following and Detached explicit in viewport state

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs`
- Create: `tests/Coda.Tui.Tests/TranscriptViewportNavigationTests.cs`
- Modify: `tests/Coda.Tui.Tests/TranscriptViewportStateUnseenTests.cs`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`

- [ ] **Step 1: Write failing pure-state transition tests**

```csharp
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

public sealed class TranscriptViewportNavigationTests
{
    private static readonly TranscriptViewportAnchor Anchor =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 2);

    [Fact]
    public void State_starts_following_at_the_bottom()
    {
        var state = NewState();

        Assert.Equal(TranscriptFollowMode.Following, state.Mode);
        Assert.Equal(state.MaxTopRow, state.TopRow);
        Assert.Null(state.DetachedAnchor);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(-100)]
    public void Every_upward_move_detaches_and_captures_the_anchor(int delta)
    {
        var state = NewState();

        state.ScrollBy(delta, Anchor);

        Assert.Equal(TranscriptFollowMode.Detached, state.Mode);
        Assert.Equal(Anchor, state.DetachedAnchor);
        Assert.True(state.TopRow < state.MaxTopRow);
    }

    [Fact]
    public void Downward_move_that_reaches_bottom_follows_and_clears_unseen_atomically()
    {
        var state = NewState();
        state.ScrollBy(-10, Anchor);
        state.OnVisibleBlockInserted();
        Assert.Equal(1, state.UnseenBlocks);

        state.ScrollToRow(state.MaxTopRow, anchor: null);

        Assert.Equal(TranscriptFollowMode.Following, state.Mode);
        Assert.Equal(0, state.UnseenBlocks);
        Assert.Equal(0, state.UnseenRows);
        Assert.Null(state.DetachedAnchor);
    }

    [Fact]
    public void Restoring_a_detached_anchor_never_changes_mode_or_unseen_count()
    {
        var state = NewState();
        state.ScrollBy(-10, Anchor);
        state.OnVisibleBlockInserted();

        state.RestoreDetachedPosition(35, Anchor);

        Assert.Equal(TranscriptFollowMode.Detached, state.Mode);
        Assert.Equal(35, state.TopRow);
        Assert.Equal(1, state.UnseenBlocks);
    }

    private static TranscriptViewportState NewState()
    {
        var state = new TranscriptViewportState();
        state.SetViewportHeight(10);
        state.SetContentRows(100);
        return state;
    }
}
```

- [ ] **Step 2: Run viewport-state tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptViewportNavigationTests|FullyQualifiedName~TranscriptViewportStateUnseenTests|FullyQualifiedName~TranscriptViewportStateTests"`

Expected: FAIL because the explicit mode, detached anchor, and anchor-aware movement methods do not exist.

- [ ] **Step 3: Implement explicit mode and one atomic follow transition**

```csharp
internal enum TranscriptFollowMode
{
    Following,
    Detached,
}
```

Add state:

```csharp
public TranscriptFollowMode Mode { get; private set; } = TranscriptFollowMode.Following;
public bool AutoFollow => this.Mode == TranscriptFollowMode.Following;
public TranscriptViewportAnchor? DetachedAnchor { get; private set; }
```

Use one method for every return to the bottom:

```csharp
private void FollowNewest()
{
    this.Mode = TranscriptFollowMode.Following;
    this.DetachedAnchor = null;
    this.UnseenRows = 0;
    this.UnseenBlocks = 0;
    this.TopRow = this.MaxTopRow;
}
```

Make movement anchor-aware:

```csharp
public void ScrollBy(int deltaRows, TranscriptViewportAnchor? anchor)
{
    if (deltaRows == 0)
    {
        return;
    }

    this.TopRow = Math.Clamp(this.TopRow + deltaRows, 0, this.MaxTopRow);
    if (this.TopRow >= this.MaxTopRow)
    {
        this.FollowNewest();
        return;
    }

    this.Mode = TranscriptFollowMode.Detached;
    this.DetachedAnchor = anchor;
}

public void ScrollToRow(int row, TranscriptViewportAnchor? anchor)
{
    this.TopRow = Math.Clamp(row, 0, this.MaxTopRow);
    if (this.TopRow >= this.MaxTopRow)
    {
        this.FollowNewest();
        return;
    }

    this.Mode = TranscriptFollowMode.Detached;
    this.DetachedAnchor = anchor;
}

public void RestoreDetachedPosition(int row, TranscriptViewportAnchor anchor)
{
    if (this.Mode != TranscriptFollowMode.Detached)
    {
        return;
    }

    this.TopRow = Math.Clamp(row, 0, this.MaxTopRow);
    this.DetachedAnchor = anchor;
}
```

`ScrollToTop` accepts/captures an anchor, `JumpToNewest` calls `FollowNewest`, and reclamping calls `FollowNewest` only when the detached viewport actually lands at the bottom.

- [ ] **Step 4: Run viewport-state tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptViewportNavigationTests|FullyQualifiedName~TranscriptViewportStateUnseenTests|FullyQualifiedName~TranscriptViewportStateTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\TranscriptViewportState.cs tests\Coda.Tui.Tests\TranscriptViewportNavigationTests.cs tests\Coda.Tui.Tests\TranscriptViewportStateUnseenTests.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs
git commit -m "feat(tui): model following and detached transcript state" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 3: Preserve the detached anchor through streaming replacement and reflow

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/InlineTuiShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/RetainedShellFixture.cs`

- [ ] **Step 1: Write failing retained-view anchor tests**

```csharp
[Fact]
public void Interior_growth_above_the_view_keeps_the_same_block_and_wrapped_offset()
{
    using var fixture = RetainedShellFixture.Create(activeWork: false);
    var view = fixture.Shell.Transcript;
    var first = new CommandOutputTranscriptBlock(Guid.NewGuid(), "first");
    var anchored = new CommandOutputTranscriptBlock(Guid.NewGuid(), "a\nb\nc\nd");
    var tail = new AssistantTranscriptBlock(Guid.NewGuid(), "tail", Complete: false);
    view.ReplaceAll([first, anchored, tail]);
    view.SetViewportHeightForTest(2);
    view.ScrollToRowForTest(3);
    var before = Assert.IsType<TranscriptViewportAnchor>(view.TopAnchorForTest);

    view.ReplaceAt(0, first with { Text = "one\ntwo\nthree\nfour\nfive" });

    Assert.Equal(before, view.TopAnchorForTest);
    Assert.Equal(TranscriptFollowMode.Detached, view.FollowModeForTest);
}

[Fact]
public void Streaming_tail_growth_does_not_move_a_detached_anchor()
{
    using var fixture = RetainedShellFixture.Create(activeWork: false);
    var view = fixture.Shell.Transcript;
    var anchored = new CommandOutputTranscriptBlock(Guid.NewGuid(), "anchor");
    var tailId = Guid.NewGuid();
    view.ReplaceAll([
        anchored,
        new AssistantTranscriptBlock(tailId, "x", Complete: false),
    ]);
    view.SetViewportHeightForTest(1);
    view.ScrollToRowForTest(0);
    var before = view.TopAnchorForTest;

    view.ReplaceLast(new AssistantTranscriptBlock(
        tailId,
        string.Join('\n', Enumerable.Repeat("stream", 20)),
        Complete: false));

    Assert.Equal(before, view.TopAnchorForTest);
}

[Fact]
public void Width_reflow_preserves_block_identity_and_clamped_wrapped_offset()
{
    using var fixture = RetainedShellFixture.Create(activeWork: false);
    var view = fixture.Shell.Transcript;
    var block = new CommandOutputTranscriptBlock(
        Guid.NewGuid(),
        "a line long enough to wrap at a narrow width");
    view.ReplaceAll([block]);
    view.SetViewportHeightForTest(1);
    view.Reflow(12);
    view.ScrollToRowForTest(2);
    var before = Assert.IsType<TranscriptViewportAnchor>(view.TopAnchorForTest);

    view.Reflow(20);

    var after = Assert.IsType<TranscriptViewportAnchor>(view.TopAnchorForTest);
    Assert.Equal(before.BlockId, after.BlockId);
    Assert.True(after.WrappedRowOffset <= before.WrappedRowOffset);
}
```

Add these explicit internal test seams to `VirtualizedTranscriptView`; they delegate to production anchor logic rather than mutating fields directly:

```csharp
internal void ScrollToRowForTest(int row) =>
    this.viewport.ScrollToRow(row, this.index.AnchorAt(row));

internal TranscriptViewportAnchor? TopAnchorForTest =>
    this.index.AnchorAt(this.viewport.TopRow);

internal TranscriptFollowMode FollowModeForTest =>
    this.viewport.Mode;
```

Extend `RetainedShellFixture.Create` with:

```csharp
Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>>?
    transcriptFormatter = null
```

pass it to `FullscreenTuiShell`, and expose `internal IApplication Application => this.app;`.

- [ ] **Step 2: Run retained-view tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Interior_growth_above_the_view|FullyQualifiedName~Streaming_tail_growth_does_not_move|FullyQualifiedName~Width_reflow_preserves|FullyQualifiedName~InlineTuiShellTests"`

Expected: FAIL because view mutations still preserve only an absolute global row.

- [ ] **Step 3: Capture and restore anchors around every layout mutation**

Add:

```csharp
private TranscriptViewportAnchor? CurrentTopAnchor() =>
    this.index.AnchorAt(this.viewport.TopRow);

private void RestoreDetachedAnchor(TranscriptViewportAnchor? priorAnchor)
{
    if (this.viewport.Mode != TranscriptFollowMode.Detached || priorAnchor is not { } anchor)
    {
        return;
    }

    if (this.index.ResolveAnchor(anchor) is { } row)
    {
        this.viewport.RestoreDetachedPosition(row, anchor);
        return;
    }

    var fallbackRow = Math.Clamp(this.viewport.TopRow, 0, this.viewport.MaxTopRow);
    var fallbackAnchor = this.index.AnchorAt(fallbackRow);
    if (fallbackAnchor is { } replacement)
    {
        this.viewport.RestoreDetachedPosition(fallbackRow, replacement);
    }
}
```

Add one atomic layout update to `TranscriptViewportState`:

```csharp
public void ApplyContentLayout(
    int contentRows,
    TranscriptViewportAnchor? detachedAnchor,
    int? resolvedAnchorRow)
{
    this.ContentRows = Math.Max(0, contentRows);
    if (this.Mode == TranscriptFollowMode.Following)
    {
        this.FollowNewest();
        return;
    }

    if (detachedAnchor is { } anchor && resolvedAnchorRow is { } row)
    {
        this.TopRow = Math.Clamp(row, 0, this.MaxTopRow);
        if (this.TopRow >= this.MaxTopRow)
        {
            this.FollowNewest();
        }
        else
        {
            this.DetachedAnchor = anchor;
        }

        return;
    }

    this.TopRow = Math.Clamp(this.TopRow, 0, this.MaxTopRow);
    if (this.TopRow >= this.MaxTopRow)
    {
        this.FollowNewest();
    }
}
```

Before `ReplaceAt`, `ReplaceLast`, `ReplaceAll` reseed reconciliation, and `Reflow`, capture `this.viewport.DetachedAnchor ?? CurrentTopAnchor()`. After changing the index, resolve the anchor and call `ApplyContentLayout` once; do not call the old reclamping setter first. `Append` while detached keeps the prior anchor; append while following stays pinned to `MaxTopRow`.

Every input method computes the post-move anchor from the index:

```csharp
private void MoveViewportBy(int rows)
{
    var target = Math.Clamp(
        this.viewport.TopRow + rows,
        0,
        this.viewport.MaxTopRow);
    this.viewport.ScrollToRow(target, this.index.AnchorAt(target));
    this.SetNeedsDraw();
    this.TranscriptScrolled?.Invoke();
}
```

Route keyboard, wheel, track click, and drag through this production method.

- [ ] **Step 4: Run retained-view tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~VirtualizedTranscriptViewTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests|FullyQualifiedName~TranscriptLayoutAnchorTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\VirtualizedTranscriptView.cs src\Coda.Tui\Ui\Shells\TranscriptViewportState.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs tests\Coda.Tui.Tests\InlineTuiShellTests.cs tests\Coda.Tui.Tests\RetainedShellFixture.cs
git commit -m "fix(tui): preserve detached transcript anchors during reflow" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 4: Make unseen counting insertion-based and atomic

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs`
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs`
- Modify: `tests/Coda.Tui.Tests/TranscriptViewportStateUnseenTests.cs`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`

- [ ] **Step 1: Write failing unseen semantic tests**

```csharp
[Fact]
public void First_visible_insert_counts_once_but_replacements_do_not()
{
    using var fixture = RetainedShellFixture.Create(activeWork: false);
    var view = fixture.Shell.Transcript;
    var seed = Enumerable.Range(0, 20)
        .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(
            Guid.NewGuid(),
            $"line {index}"))
        .ToImmutableArray();
    view.ReplaceAll(seed);
    view.SetViewportHeightForTest(3);
    view.ScrollBy(-5);

    var activityId = Guid.NewGuid();
    var running = new ToolTranscriptBlock(
        activityId, "grep", "{}", null, null, false, false);
    view.Append(running);
    view.ReplaceLast(running with { ElapsedMs = 500 });
    view.ReplaceLast(running with { Result = "done", Complete = true });

    Assert.Equal(1, view.UnseenBlocks);
}

[Fact]
public void Hidden_block_and_separator_layout_do_not_count_as_messages()
{
    using var fixture = RetainedShellFixture.Create(
        activeWork: false,
        transcriptFormatter: (block, width) =>
            block is ToolTranscriptBlock
                ? []
                : TranscriptBlockFormatter.Format(block, width));
    var view = fixture.Shell.Transcript;
    view.ReplaceAll(Enumerable.Range(0, 20)
        .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(
            Guid.NewGuid(),
            $"line {index}"))
        .ToImmutableArray());
    view.SetViewportHeightForTest(3);
    view.ScrollBy(-5);

    view.Append(new ToolTranscriptBlock(
        Guid.NewGuid(), "hidden", "{}", null, null, false, false));

    Assert.Equal(0, view.UnseenBlocks);
}
```

- [ ] **Step 2: Run unseen tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptViewportStateUnseenTests|FullyQualifiedName~First_visible_insert_counts_once|FullyQualifiedName~Hidden_block_and_separator"`

Expected: FAIL until insertions, replacements, and hidden zero-row blocks are distinguished explicitly.

- [ ] **Step 3: Centralize visible insertion and atomic clearing**

Rename the state signal to describe the approved behavior:

```csharp
public void OnVisibleBlockInserted()
{
    if (this.Mode == TranscriptFollowMode.Detached)
    {
        this.UnseenBlocks++;
    }
}
```

In `VirtualizedTranscriptView.Append`, invoke it only when the newly formatted block contributes at least one semantic row:

```csharp
var before = this.index.TotalRows;
this.index.Append(block, this.currentWidth);
var delta = this.index.TotalRows - before;
this.viewport.OnRowsAppended(delta);
if (delta > 0)
{
    this.viewport.OnVisibleBlockInserted();
}
```

Do not invoke it from `ReplaceLast`, `ReplaceAt`, `Reflow`, or separator generation. All paths that reach the bottom call the single `FollowNewest` transition from Task 2 so unseen rows, unseen blocks, mode, anchor, and top row change together.

- [ ] **Step 4: Run unseen tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptViewportStateUnseenTests|FullyQualifiedName~First_visible_insert_counts_once|FullyQualifiedName~Hidden_block_and_separator|FullyQualifiedName~FullscreenTuiShellTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\TranscriptViewportState.cs src\Coda.Tui\Ui\Shells\VirtualizedTranscriptView.cs tests\Coda.Tui.Tests\TranscriptViewportStateUnseenTests.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs
git commit -m "fix(tui): count only new visible transcript blocks" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 5: Give the jump control a dedicated row and exact rendered hit target

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/JumpToBottomHint.cs`
- Modify: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs`
- Modify: `tests/Coda.Tui.Tests/JumpToBottomHintTests.cs`
- Modify: `tests/Coda.Tui.Tests/TranscriptNavigationChromeTests.cs`
- Modify: `tests/Coda.Tui.Tests/InlineTuiShellTests.cs`

- [ ] **Step 1: Write failing label, geometry, and hit-testing tests**

```csharp
public sealed class JumpToBottomHintTests
{
    [Theory]
    [InlineData(0, "Jump to bottom (Ctrl+End) v")]
    [InlineData(1, "1 new message (Ctrl+End) v")]
    [InlineData(3, "3 new messages (Ctrl+End) v")]
    public void Hint_text_matches_the_approved_labels(int unseen, string expected) =>
        Assert.Equal(expected, JumpToBottomHint.HintText(unseen));

    [Fact]
    public void Clicks_outside_the_rendered_text_do_not_jump()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        var hint = fixture.Shell.JumpHint;
        var jumps = 0;
        hint.Jump += () => jumps++;
        hint.Update(autoFollow: false, unseenBlockCount: 3);
        fixture.Application.LayoutAndDraw();
        var target = hint.RenderedHitTargetForTest;

        hint.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonClicked,
            Position = new Point(Math.Max(0, target.Left - 1), 0),
        });
        Assert.Equal(0, jumps);

        hint.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonClicked,
            Position = new Point(target.Left, 0),
        });
        Assert.Equal(1, jumps);
    }
}

[Fact]
public void Jump_control_uses_a_dedicated_row_above_composer_chrome()
{
    using var fixture = RetainedShellFixture.Create(activeWork: false);
    fixture.Application.LayoutAndDraw();

    Assert.Equal(
        fixture.Shell.Chrome.Frame.Y - 1,
        fixture.Shell.JumpHint.Frame.Y);
    Assert.NotEqual(
        fixture.Shell.Status.Frame.Y,
        fixture.Shell.JumpHint.Frame.Y);
}
```

- [ ] **Step 2: Run jump-control tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~JumpToBottomHintTests|FullyQualifiedName~Jump_control_uses_a_dedicated_row|FullyQualifiedName~InlineTuiShellTests"`

Expected: FAIL because the current label uses a different glyph, the whole row is clickable, and the control is anchored onto the shell’s final row.

- [ ] **Step 3: Implement cell-aware hit geometry and a reserved navigation row**

Add a typed hit target:

```csharp
internal readonly record struct JumpHintHitTarget(int Left, int Width)
{
    public bool Contains(Point point) =>
        point.Y == 0 &&
        point.X >= this.Left &&
        point.X < this.Left + this.Width;
}
```

In `JumpToBottomHint`, calculate the displayed text with `TerminalCellText.SliceByCells`, center using `TerminalCellText.Width`, store the resulting `JumpHintHitTarget`, and invoke `Jump` only when `Visible` and the click falls inside it.

Expose:

```csharp
private JumpHintHitTarget renderedHitTarget;

internal JumpHintHitTarget RenderedHitTargetForTest =>
    this.renderedHitTarget;
```

Use the approved labels:

```csharp
public static string HintText(int unseenBlocks) => unseenBlocks <= 0
    ? "Jump to bottom (Ctrl+End) v"
    : $"{unseenBlocks} new message{(unseenBlocks == 1 ? string.Empty : "s")} (Ctrl+End) v";
```

Reserve one row immediately above composer chrome:

```csharp
private const int NavigationChromeHeight = 1;

private void ApplyBottomAnchors()
{
    this.transcript.Height = Dim.Fill(this.composerHeight + 5);
    this.Operational.Y = Pos.AnchorEnd(this.composerHeight + 5);
    this.jumpHint.Y = Pos.AnchorEnd(this.composerHeight + 4);
    this.Chrome.Y = Pos.AnchorEnd(this.composerHeight + 3);
    this.Chrome.Height = this.composerHeight + 2;
    this.Composer.Y = Pos.AnchorEnd(this.composerHeight + 2);
    this.Composer.Height = this.composerHeight;
    this.Completion.Y = Pos.AnchorEnd(this.composerHeight + 5);
}
```

Also update `PlaceCompletion`:

```csharp
this.Completion.Y =
    Pos.AnchorEnd(this.composerHeight + height + 5);
```

The row remains reserved while hidden, preventing viewport/status overlap and layout-cell ambiguity. Inline inherits this layout from `FullscreenTuiShell`.

- [ ] **Step 4: Run jump-control tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~JumpToBottomHintTests|FullyQualifiedName~TranscriptNavigationChromeTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\JumpToBottomHint.cs src\Coda.Tui\Ui\Shells\FullscreenTuiShell.cs tests\Coda.Tui.Tests\JumpToBottomHintTests.cs tests\Coda.Tui.Tests\TranscriptNavigationChromeTests.cs tests\Coda.Tui.Tests\InlineTuiShellTests.cs
git commit -m "fix(tui): reserve precise jump-to-bottom navigation chrome" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 6: Route global bottom navigation while respecting modal overlays

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs`
- Modify: `tests/Coda.Tui.Tests/TranscriptNavigationChromeTests.cs`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/InlineTuiShellTests.cs`

- [ ] **Step 1: Write failing focus and modal-isolation tests**

```csharp
[Fact]
public void Up_page_up_and_home_detach_while_end_at_bottom_restores_following()
{
    using var fixture = RetainedShellFixture.Create(activeWork: false);
    var view = fixture.Shell.Transcript;
    view.ReplaceAll(Enumerable.Range(0, 50)
        .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(
            Guid.NewGuid(),
            $"line {index}"))
        .ToImmutableArray());

    foreach (var key in new[] { Key.CursorUp, Key.PageUp, Key.Home })
    {
        view.JumpToNewest();
        view.NewKeyDownEvent(key);
        Assert.False(view.AutoFollow);
    }

    view.NewKeyDownEvent(Key.End);
    Assert.True(view.AutoFollow);
}

[Theory]
[InlineData(true)]
[InlineData(false)]
public void Ctrl_end_reaches_bottom_from_composer_or_transcript_focus(bool composerFocus)
{
    using var fixture = RetainedShellFixture.Create(activeWork: false);
    var view = fixture.Shell.Transcript;
    view.ReplaceAll(Enumerable.Range(0, 50)
        .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(
            Guid.NewGuid(),
            $"line {index}"))
        .ToImmutableArray());
    view.ScrollBy(-10);
    if (composerFocus)
    {
        fixture.Shell.Composer.SetFocus();
        fixture.Shell.Composer.NewKeyDownEvent(Key.End.WithCtrl);
    }
    else
    {
        view.SetFocus();
        view.NewKeyDownEvent(Key.End.WithCtrl);
    }

    Assert.True(view.AutoFollow);
    Assert.Equal(0, view.UnseenBlocks);
}

[Fact]
public void Ctrl_end_does_not_move_the_transcript_behind_a_task_overlay()
{
    var tasks = new TaskManager(sessionId: "navigation", logRoot: null);
    var executionGate = new AgentExecutionGate();
    using var fixture = RetainedShellFixture.Create(
        activeWork: false,
        taskBrowserProvider: () =>
            new TaskBrowserProvider(tasks, executionGate));
    var view = fixture.Shell.Transcript;
    view.ReplaceAll(Enumerable.Range(0, 50)
        .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(
            Guid.NewGuid(),
            $"line {index}"))
        .ToImmutableArray());
    view.ScrollBy(-10);
    var top = view.TopRow;
    fixture.Shell.TaskOverlay!.Show();

    fixture.Shell.TaskOverlay.NewKeyDownEvent(Key.End.WithCtrl);

    Assert.Equal(top, view.TopRow);
    Assert.False(view.AutoFollow);
}

[Fact]
public async Task Ctrl_end_does_not_move_the_transcript_behind_a_prompt_overlay()
{
    using var fixture = RetainedShellFixture.Create(activeWork: false);
    var view = fixture.Shell.Transcript;
    view.ReplaceAll(Enumerable.Range(0, 50)
        .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(
            Guid.NewGuid(),
            $"line {index}"))
        .ToImmutableArray());
    view.ScrollBy(-10);
    var top = view.TopRow;
    await fixture.Shell.ApplyAsync(
        fixture.Shell.Snapshot with
        {
            PendingPrompt = UiPromptRequest.Confirm(
                "Confirm?",
                defaultValue: false),
        },
        CancellationToken.None);

    fixture.Shell.PromptOverlay.NewKeyDownEvent(Key.End.WithCtrl);

    Assert.Equal(top, view.TopRow);
    Assert.False(view.AutoFollow);
}
```

- [ ] **Step 2: Run global-navigation tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Ctrl_end_reaches_bottom|FullyQualifiedName~Ctrl_end_does_not_move|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests"`

Expected: FAIL if any main focus path misses the shell handler or a modal lets the shortcut leak through.

- [ ] **Step 3: Centralize shortcut eligibility and bottom arrival**

Add:

```csharp
private bool HasVisibleModalOverlay() =>
    this.PromptOverlay.Visible ||
    this.TaskOverlay?.Visible == true;

private bool TryHandleTranscriptNavigationKey(Key key)
{
    if (this.HasVisibleModalOverlay() || key != Key.End.WithCtrl)
    {
        return false;
    }

    this.TranscriptView.JumpToNewest();
    return true;
}
```

Call this from the shared shell key path before composer-specific handling. Keep `VirtualizedTranscriptView.OnKeyDown(Key.End.WithCtrl)` as the transcript-focused equivalent; both routes call the same `JumpToNewest`, which raises `TranscriptScrolled` and refreshes the jump control immediately.

In `VirtualizedTranscriptView.OnKeyDown`, map plain Home and `Ctrl+Home` to row 0, plain End to `MaxTopRow`, Up/PageUp to negative movement, and Down/PageDown to positive movement. All use the same anchor-aware movement method; reaching `MaxTopRow` restores Following atomically.

When the MCP overlay from the MCP-manager plan is present, extend `HasVisibleModalOverlay` with `this.McpOverlay?.Visible == true`; do not create a parallel shortcut state machine.

- [ ] **Step 4: Run global-navigation tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptNavigationChromeTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests|FullyQualifiedName~TasksInterceptTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\TerminalGuiShellBase.cs src\Coda.Tui\Ui\Shells\VirtualizedTranscriptView.cs tests\Coda.Tui.Tests\TranscriptNavigationChromeTests.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs tests\Coda.Tui.Tests\InlineTuiShellTests.cs
git commit -m "fix(tui): route global transcript bottom navigation safely" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 7: Harden scrollbar dragging and mouse-capture teardown

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs`
- Modify: `src/Coda.Tui/Ui/Shells/ScrollbarMetrics.cs`
- Modify: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs`
- Modify: `tests/Coda.Tui.Tests/ScrollbarMetricsTests.cs`
- Modify: `tests/Coda.Tui.Tests/TranscriptNavigationChromeTests.cs`

- [ ] **Step 1: Write failing paging, drag, release, and disposal tests**

```csharp
[Fact]
public void Upward_track_click_detaches_and_downward_track_click_can_restore_following()
{
    using var fixture = RetainedShellFixture.Create(activeWork: false);
    var view = SeedOverflowingView(fixture, blockCount: 80);
    fixture.HostApplication.Mouse.IsMouseDisabled = false;
    var x = view.Frame.Width - 1;
    var height = view.ViewportHeightForTest;

    view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonPressed,
        Position = new Point(x, 0),
    });
    Assert.False(view.AutoFollow);

    view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonPressed,
        Position = new Point(x, height - 1),
    });
    Assert.True(view.TopRow > 0);
}

[Fact]
public void Button_up_and_dispose_always_release_scrollbar_capture()
{
    var fixture = RetainedShellFixture.Create(activeWork: false);
    var view = SeedOverflowingView(fixture, blockCount: 80);
    fixture.HostApplication.Mouse.IsMouseDisabled = false;
    var metrics = ScrollbarMetrics.Compute(
        view.ContentRowsForTest,
        view.ViewportHeightForTest,
        view.TopRow);

    view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonPressed,
        Position = new Point(view.Frame.Width - 1, metrics.ThumbTop),
    });
    Assert.True(view.MouseCaptureActiveForTest);

    view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonReleased,
        Position = new Point(view.Frame.Width - 1, metrics.ThumbTop),
    });
    Assert.False(view.MouseCaptureActiveForTest);

    view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonPressed,
        Position = new Point(view.Frame.Width - 1, metrics.ThumbTop),
    });
    fixture.Dispose();
    Assert.False(view.MouseCaptureActiveForTest);
}

private static VirtualizedTranscriptView SeedOverflowingView(
    RetainedShellFixture fixture,
    int blockCount)
{
    var view = fixture.Shell.Transcript;
    view.ReplaceAll(Enumerable.Range(0, blockCount)
        .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(
            Guid.NewGuid(),
            $"line {index}"))
        .ToImmutableArray());
    view.SetViewportHeightForTest(5);
    return view;
}
```

- [ ] **Step 2: Run scrollbar tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ScrollbarMetricsTests|FullyQualifiedName~Upward_track_click_detaches|FullyQualifiedName~Button_up_and_dispose"`

Expected: FAIL because capture release is not centralized and disabled/teardown paths can bypass button-up cleanup.

- [ ] **Step 3: Release capture before disabled-input exits and during teardown**

Add:

```csharp
private void ReleaseMouseCapture()
{
    if (!this.scrollbarDragging && !this.dragging)
    {
        return;
    }

    this.scrollbarDragging = false;
    this.dragging = false;
    this.app.Mouse?.UngrabMouse();
}

internal void CancelMouseInteraction() => this.ReleaseMouseCapture();

internal bool MouseCaptureActiveForTest =>
    this.scrollbarDragging || this.dragging;
```

At the top of `ProcessMouse`, process `LeftButtonReleased` for an active drag before checking `IsMouseDisabled` or Shift. During thumb motion, resolve the target row through `ScrollbarMetrics.TopRowForPointer` and pass `index.AnchorAt(target)` into viewport state. Track clicks page by one viewport height through the same movement method used by keyboard and wheel input.

Override disposal:

```csharp
protected override void Dispose(bool disposing)
{
    if (disposing)
    {
        this.ReleaseMouseCapture();
    }

    base.Dispose(disposing);
}
```

Call `transcript.CancelMouseInteraction()` from retained-shell teardown before unbinding events. A mode switch disposes the old shell, so it follows this same path.

- [ ] **Step 4: Run scrollbar tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ScrollbarMetricsTests|FullyQualifiedName~TranscriptNavigationChromeTests|FullyQualifiedName~VirtualizedTranscriptViewTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\VirtualizedTranscriptView.cs src\Coda.Tui\Ui\Shells\ScrollbarMetrics.cs src\Coda.Tui\Ui\Shells\FullscreenTuiShell.cs tests\Coda.Tui.Tests\ScrollbarMetricsTests.cs tests\Coda.Tui.Tests\TranscriptNavigationChromeTests.cs
git commit -m "fix(tui): release transcript scrollbar capture on every exit" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 8: Reserve an extra timestamp-to-scrollbar gap

**Files:**
- Modify: `src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs`
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs`
- Modify: `tests/Coda.Tui.Tests/TranscriptBlockFormatterTests.cs`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`

- [ ] **Step 1: Write failing formatter and drawing-position tests**

```csharp
[Fact]
public void User_timestamp_reserves_one_trailing_cell_after_the_annotation()
{
    var block = new UserTranscriptBlock(
        Guid.NewGuid(),
        "hello",
        new DateTimeOffset(2026, 7, 22, 16, 40, 0, TimeSpan.Zero));

    var first = TranscriptBlockFormatter.Format(block, width: 20)[0];

    Assert.Equal("16:40", first.RightText);
    Assert.Equal(1, first.RightTextTrailingCells);
}

[Theory]
[InlineData(5)]
[InlineData(6)]
[InlineData(8)]
[InlineData(12)]
public void Narrow_width_either_keeps_the_full_gap_or_omits_the_timestamp(
    int width)
{
    var block = new UserTranscriptBlock(
        Guid.NewGuid(),
        "hello",
        new DateTimeOffset(2026, 7, 22, 16, 40, 0, TimeSpan.Zero));

    var first = TranscriptBlockFormatter.Format(block, width)[0];

    if (first.RightText is { } timestamp)
    {
        Assert.True(
            TerminalCellText.Width(first.Text) +
            1 +
            TerminalCellText.Width(timestamp) +
            first.RightTextTrailingCells <= width);
    }
}

[Fact]
public async Task Drawn_timestamp_leaves_a_blank_cell_before_the_scrollbar()
{
    using var fixture = RetainedShellFixture.Create(activeWork: false);
    var blocks = Enumerable.Range(0, 40)
        .Select(index => (TranscriptBlock)new UserTranscriptBlock(
            Guid.NewGuid(),
            $"message {index}",
            new DateTimeOffset(2026, 7, 22, 16, index % 60, 0, TimeSpan.Zero)))
        .ToImmutableArray();
    await fixture.Shell.ApplyAsync(
        UiSessionSnapshot.Empty with { Transcript = blocks },
        CancellationToken.None);
    fixture.Application.LayoutAndDraw();

    var scrollbarColumn = fixture.Shell.Transcript.Frame.Width - 1;
    var annotationEnd = fixture.Shell.Transcript.LastRightAnnotationEndColumnForTest;
    Assert.Equal(scrollbarColumn - 2, annotationEnd);
}
```

- [ ] **Step 2: Run timestamp tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~User_timestamp_reserves_one_trailing_cell|FullyQualifiedName~Drawn_timestamp_leaves_a_blank_cell|FullyQualifiedName~Resumed_user_message_without_a_timestamp"`

Expected: FAIL because render rows carry no trailing-cell reservation and annotations currently touch the scrollbar-adjacent content edge.

- [ ] **Step 3: Carry and apply typed right-annotation padding**

Add to both `TranscriptRenderLine` and `TranscriptRow`:

```csharp
public int RightTextTrailingCells { get; init; }
```

Pass it through `TranscriptLayoutIndex.CollectRows`. In `AppendUser`, reserve text/annotation separation plus one trailing visual cell:

```csharp
const int TextToTimestampGap = 1;
const int TimestampTrailingGap = 1;
var reserved =
    TerminalCellText.Width(time) +
    TextToTimestampGap +
    TimestampTrailingGap;
```

Set `RightTextTrailingCells = TimestampTrailingGap` on the first timestamped row. In `VirtualizedTranscriptView.DrawRow`, place the annotation at:

```csharp
var column =
    viewWidth -
    row.RightTextTrailingCells -
    annotationWidth;
this.LastRightAnnotationEndColumnForTest =
    column + annotationWidth - 1;
```

If that position is negative, omit the annotation as today. Keep pending annotations unchanged unless they also sit beside a visible scrollbar in a failing regression.

Add:

```csharp
internal int LastRightAnnotationEndColumnForTest { get; private set; } = -1;
```

Reset it to `-1` at the start of `OnDrawingContent`.

- [ ] **Step 4: Run timestamp and retained-shell tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptBlockFormatterTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Rendering\TranscriptBlockFormatter.cs src\Coda.Tui\Ui\Shells\TranscriptLayoutIndex.cs src\Coda.Tui\Ui\Shells\VirtualizedTranscriptView.cs tests\Coda.Tui.Tests\TranscriptBlockFormatterTests.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs
git commit -m "fix(tui): keep transcript timestamps clear of the scrollbar" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Subsystem completion checks

Run from `C:\Users\yurio\Documents\github\coda-cli`:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptLayoutAnchorTests|FullyQualifiedName~TranscriptViewportNavigationTests|FullyQualifiedName~TranscriptViewportStateUnseenTests|FullyQualifiedName~TranscriptNavigationChromeTests|FullyQualifiedName~JumpToBottomHintTests|FullyQualifiedName~ScrollbarMetricsTests|FullyQualifiedName~TranscriptBlockFormatterTests|FullyQualifiedName~VirtualizedTranscriptViewTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests|FullyQualifiedName~TasksInterceptTests"
```

Expected: PASS with stable detached anchors during streaming/reflow, one-time unseen increments, exact jump hit-testing, modal-safe `Ctrl+End`, released mouse capture, and identical inline/fullscreen behavior.
