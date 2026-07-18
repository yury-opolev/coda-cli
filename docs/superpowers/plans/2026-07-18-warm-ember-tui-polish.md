# Warm Ember TUI Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Polish the retained Terminal.Gui UI with the Warm Ember semantic palette, full-width transcript, dedicated operational row, dynamic multiline composer, composer-first focus, safe exit/interrupt chords, and selectable/copyable transcript text.

**Architecture:** Keep `UiSessionSnapshot` semantic and immutable while adding pure presentation projectors/layout models plus shell-local focus, timer, chord, composer-scroll, and transcript-selection state. Both retained shells continue sharing one layout and virtualized transcript; only Terminal.Gui views resolve theme attributes, own timers, route input, and call the public clipboard API. Plain and Spectre modes remain outside the retained-view lifecycle.

**Tech Stack:** C# / .NET 10, Terminal.Gui 2.4.17 public instance APIs, `TextView`, immutable records/arrays, `TimeProvider` monotonic timestamps, xUnit, isolated ANSI-driver and `BeginInit`/`EndInit`/`Layout` tests.

**Approved design:** `docs/superpowers/specs/2026-07-18-warm-ember-tui-polish-design.md`

---

## Baseline and non-negotiable constraints

- Work from branch `fix/tui-startup-banner` in `C:\Users\yurio\Documents\github\coda-cli\.worktrees\tui-fullscreen-default`.
- Treat commits `711e681` (startup banner) and `e604a10` (startup-aware composer chrome) as the baseline. Refactor their chrome: remove the accent bar and duplicate chrome `Initializing…`; keep startup input blocking, move initialization text into the operational row, and leave a blank dark composer region while startup runs.
- Keep Terminal.Gui pinned to public package version `2.4.17`. Do not use unpublished fake drivers, reflection, `Activator`, or private-member access. View tests use `BeginInit`/`EndInit`/`Layout` or `Application.Create()` plus `DriverRegistry.Names.ANSI`.
- Preserve the virtualized transcript's bounded viewport rendering, LRU wrapped-block cache, append/replace-tail/interior incremental paths, and resize reflow in inline and full-screen modes.
- Preserve plain, redirected, and Spectre behavior. Those modes must not construct retained views, theme timers, chord timers, or composer layout state.
- Presentation-only state (`OperationalStatus`, composer layout/scroll, chord state, transcript selection) must not enter `UiSessionSnapshot`.
- Use `IApplication.Clipboard?.TrySetClipboardData(text)` for production clipboard writes.
- Keep the minimum interactive terminal size at 60×12.
- Do not modify `version.json` or `version.props`, do not add a packaging/version-bump task, and do not add a local install step. Version `0.1.68` remains unchanged unless packaging is requested separately.
- Every implementation task follows red/green/refactor: add the named failing test, run the exact targeted command and observe the stated failure, add only the specified implementation, rerun green, then commit.

## File map

### New production files

- `src/Coda.Tui/Ui/Rendering/TerminalCellText.cs` — one grapheme/cell-width implementation shared by transcript wrapping, composer wrapping/navigation, selection slicing, and completion clipping.
- `src/Coda.Tui/Ui/Rendering/TuiTheme.cs` — Warm Ember semantic colors, RGB values, named 16-color fallbacks, attribute/scheme factories, and driver-aware truecolor resolution.
- `src/Coda.Tui/Ui/State/OperationalStatus.cs` — immutable `OperationalStatus(string Text, OperationalTone Tone, bool Animated)` display model.
- `src/Coda.Tui/Ui/State/OperationalStatusProjector.cs` — pure priority projection from `UiSessionSnapshot`.
- `src/Coda.Tui/Ui/Shells/OperationalStatusView.cs` — one-row themed renderer with a low-frequency spinner whose timeout exists only for animated status.
- `src/Coda.Tui/Ui/Input/ComposerVisualLayout.cs` — grapheme/cell-aware wrapped rows and UTF-16-index ↔ visual-position mapping.
- `src/Coda.Tui/Ui/Shells/ShellCommandChordState.cs` — monotonic Esc/Ctrl+C arming windows and operational hints.
- `src/Coda.Tui/Ui/Shells/TranscriptSelection.cs` — shell-local global-row/cell-column selection and row-range/copy calculations.

### New test files

- `tests/Coda.Tui.Tests/TerminalCellTextTests.cs`
- `tests/Coda.Tui.Tests/TuiThemeTests.cs`
- `tests/Coda.Tui.Tests/OperationalStatusProjectorTests.cs`
- `tests/Coda.Tui.Tests/OperationalStatusViewTests.cs`
- `tests/Coda.Tui.Tests/ComposerVisualLayoutTests.cs`
- `tests/Coda.Tui.Tests/ShellCommandChordStateTests.cs`
- `tests/Coda.Tui.Tests/TranscriptSelectionTests.cs`
- `tests/Coda.Tui.Tests/ManualTimeProvider.cs`
- `tests/Coda.Tui.Tests/RetainedShellFixture.cs`

### Modified production files

- `src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs:398-578` — replace private duplicate width/grapheme logic with `TerminalCellText`.
- `src/Coda.Tui/Ui/State/StatusProjector.cs:45-95` — retain stable metadata only; remove `ActiveOperation.Label`.
- `src/Coda.Tui/Ui/Input/ComposerState.cs:11-18` — carry internal scroll row and preferred visual column across mode switches.
- `src/Coda.Tui/Ui/Input/UiAction.cs:9-37` — add visual up/down actions; stop treating Ctrl+C/Ctrl+D as composer actions.
- `src/Coda.Tui/Ui/Input/UiActionMap.cs:17-120` — completion/editor/history precedence and explicit Ctrl+Up/Down history.
- `src/Coda.Tui/Ui/Input/ComposerController.cs:36-337` — preferred-column-aware cursor seam, viewport state, and navigation resets.
- `src/Coda.Tui/Ui/Input/ComposerView.cs:20-387` — laid-out visual caret mapping, dynamic-height invalidation, internal scroll, shell-first chord routing, and state capture.
- `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs:24-363` — theme attributes, drag selection, selected-cell drawing, arbitrary-range copy, and unhandled-key routing.
- `src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs:150-217` — arbitrary global-row-range accessor not clamped to the viewport.
- `src/Coda.Tui/Ui/Shells/ComposerChromeView.cs:8-175` — final blank/`>` chrome with no accent bar and theme-owned dark background.
- `src/Coda.Tui/Ui/Shells/CommandCompletionView.cs:19-181` — Warm Ember normal/selected attributes and shared cell-aware clipping.
- `src/Coda.Tui/Ui/Shells/PromptOverlay.cs:21-327` — Warm Ember prompt/approval scheme.
- `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs:20-349` — operational row, projected/local status priority, timer/chord/input routing, focus restoration, clipboard arbitration, and disposal.
- `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs:26-239` — full-width transcript and dynamic bottom-anchor geometry.
- `src/Coda.Tui/Ui/Shells/InlineTuiShell.cs:21-45` — updated minimum retained height and inherited dynamic geometry.
- `src/Coda.Tui/Ui/TuiController.cs:35-340` — second-key-only interrupt/exit handling; remove the old idle Ctrl+C notification path.
- `src/Coda.Tui/InteractiveProgram.cs:322-365` — inject active-work and clipboard/chord defaults into retained shells without changing plain/Spectre composition.
- `src/Coda.Tui/ImmediateCli.cs:39-74` — document retained keybindings and mouse-selection fallback.
- `README.md:96-128` — Warm Ember layout, dynamic composer, focus, chords, and copy behavior.
- `docs/terminal-gui-compatibility.md:28-117` — revised manual acceptance items for truecolor/fallback, cursor, resizing, chords, and selection.
- `scripts/terminal-gui-pty-smoke.ps1:54-79` — operator wording aligned with the new chord/copy behavior.
- `samples/Coda.TerminalGuiSpike/HarnessOptions.cs:163-195` and `samples/Coda.TerminalGuiSpike/SpikeHarness.cs:271-303` — compatibility help/cancel wording only; no production semantics.

### Modified tests

- `tests/Coda.Tui.Tests/TranscriptBlockFormatterTests.cs`
- `tests/Coda.Tui.Tests/StatusProjectorTests.cs`
- `tests/Coda.Tui.Tests/ComposerControllerTests.cs`
- `tests/Coda.Tui.Tests/ComposerViewTests.cs`
- `tests/Coda.Tui.Tests/ComposerChromeViewTests.cs`
- `tests/Coda.Tui.Tests/CommandCompletionViewTests.cs`
- `tests/Coda.Tui.Tests/CommandCompletionShellTests.cs`
- `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`
- `tests/Coda.Tui.Tests/InlineTuiShellTests.cs`
- `tests/Coda.Tui.Tests/TuiControllerTests.cs`
- `tests/Coda.Tui.Tests/ImmediateCliTests.cs`
- `tests/Coda.Tui.Tests/TerminalGuiModeRunnerTests.cs`

## Final public/internal signatures

These names are fixed for all tasks below; do not introduce parallel aliases.

```csharp
internal readonly record struct TuiThemeColor(
    Terminal.Gui.Drawing.Color TrueColor,
    Terminal.Gui.Drawing.ColorName16 Fallback);

internal enum OperationalTone
{
    Ready,
    Initializing,
    Working,
    Thinking,
    Waiting,
    Approval,
    Warning,
    Error,
}

internal sealed record OperationalStatus(string Text, OperationalTone Tone, bool Animated);

internal static class OperationalStatusProjector
{
    public static OperationalStatus Project(UiSessionSnapshot snapshot);
}

internal readonly record struct ComposerVisualPosition(int Row, int Column);

internal sealed class ComposerVisualLayout
{
    public static ComposerVisualLayout Create(string text, int width);
    public int VisualLineCount { get; }
    public ComposerVisualPosition PositionForIndex(int utf16Index);
    public int IndexForPosition(int row, int displayColumn);
    public (int CursorIndex, int PreferredColumn) MoveVertical(
        int utf16Index,
        int delta,
        int? preferredColumn);
}

public readonly record struct UiInputContext(
    bool ComposerEmpty,
    bool CompletionVisible,
    bool CanMoveVisualUp,
    bool CanMoveVisualDown);

public sealed record ComposerState(
    string Draft,
    int CursorIndex,
    ImmutableArray<string> History,
    int HistoryIndex,
    bool PasteActive,
    int ScrollRow = 0,
    int? PreferredDisplayColumn = null);

internal readonly record struct TranscriptCellPosition(int GlobalRow, int CellColumn);

internal sealed class TranscriptSelection
{
    internal bool HasSelection { get; }
    internal TranscriptCellPosition Anchor { get; }
    internal TranscriptCellPosition Active { get; }
    internal void Begin(TranscriptCellPosition anchor);
    internal bool Update(TranscriptCellPosition active);
    internal void Clear();
    internal (int StartCell, int EndCellExclusive)? RangeForRow(int globalRow, int rowWidth);
}

internal enum ShellChordAction
{
    None,
    Interrupt,
    Exit,
}

internal readonly record struct ShellChordResult(
    bool Consumed,
    ShellChordAction Action,
    OperationalStatus? Hint);
```

`FullscreenTuiShell` constructor parameter order is exactly:
`(IApplication app, ComposerController controller, IUiEventPublisher publisher, UiSessionSnapshot initialSnapshot, Func<bool>? hasActiveWork = null, TimeProvider? timeProvider = null, Func<string, bool>? clipboardWriter = null, Func<TimeSpan, Func<bool>, object>? addTimeout = null, Func<object, bool>? removeTimeout = null, TuiTheme? theme = null, Func<UiSessionSnapshot, int, string>? statusProjection = null, Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>>? transcriptFormatter = null)`.

`InlineTuiShell` uses the same order through `statusProjection` and forwards those arguments to `FullscreenTuiShell`; it does not add a second theme, timer, chord, or clipboard seam.

---

### Task 1: Centralize grapheme and terminal-cell calculations

**Files:**
- Create: `src/Coda.Tui/Ui/Rendering/TerminalCellText.cs`
- Create: `tests/Coda.Tui.Tests/TerminalCellTextTests.cs`
- Modify: `src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs:398-578`
- Modify: `tests/Coda.Tui.Tests/TranscriptBlockFormatterTests.cs`

- [ ] **Step 1: Write failing width, slicing, and wrapping tests**

```csharp
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

public sealed class TerminalCellTextTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("界", 2)]
    [InlineData("\U0001F600", 2)]
    [InlineData("e\u0301", 1)]
    public void Width_counts_terminal_cells_without_splitting_graphemes(string text, int expected)
    {
        Assert.Equal(expected, TerminalCellText.Width(text));
    }

    [Fact]
    public void SliceByCells_selects_whole_graphemes_that_intersect_the_range()
    {
        const string text = "a界e\u0301z";

        Assert.Equal("界", TerminalCellText.SliceByCells(text, 1, 3));
        Assert.Equal("e\u0301", TerminalCellText.SliceByCells(text, 3, 4));
        Assert.Equal("界e\u0301", TerminalCellText.SliceByCells(text, 2, 4));
    }

    [Fact]
    public void Wrap_preserves_newlines_prefers_whitespace_and_hard_breaks_long_grapheme_runs()
    {
        var rows = TerminalCellText.Wrap("alpha beta\n界界界", width: 6);

        Assert.Collection(
            rows,
            row => Assert.Equal(("alpha", 0, 5, 5), (row.Text, row.StartIndex, row.EndIndex, row.CellWidth)),
            row => Assert.Equal(("beta", 6, 10, 4), (row.Text, row.StartIndex, row.EndIndex, row.CellWidth)),
            row => Assert.Equal(("界界界", 11, 14, 6), (row.Text, row.StartIndex, row.EndIndex, row.CellWidth)));
    }
}
```

Add this regression to `TranscriptBlockFormatterTests.cs`:

```csharp
[Fact]
public void Formatter_uses_shared_cell_width_for_wide_and_combining_graphemes()
{
    var block = new UserTranscriptBlock(Guid.NewGuid(), "界界e\u0301");

    var lines = TranscriptBlockFormatter.Format(block, width: 4);

    Assert.Equal(["界界", "e\u0301"], lines.Select(line => line.Text));
}
```

- [ ] **Step 2: Run the focused tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TerminalCellTextTests|FullyQualifiedName~TranscriptBlockFormatterTests.Formatter_uses_shared_cell_width" --no-restore
```

Expected: FAIL to compile because `TerminalCellText`, `WrappedCellRow`, and their methods do not exist.

- [ ] **Step 3: Add the shared implementation and remove the formatter duplicates**

Create `TerminalCellText.cs` with these exact public-to-assembly members:

```csharp
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Coda.Tui.Ui.Rendering;

internal readonly record struct TerminalTextElement(
    string Text,
    int Utf16Start,
    int Utf16Length,
    int CellStart,
    int CellWidth);

internal readonly record struct WrappedCellRow(
    string Text,
    int StartIndex,
    int EndIndex,
    int CellWidth,
    ImmutableArray<TerminalTextElement> Elements);

internal static class TerminalCellText
{
    public static int Width(string text) =>
        Enumerate(text ?? string.Empty).Sum(element => element.CellWidth);

    public static ImmutableArray<TerminalTextElement> Enumerate(string text)
    {
        text ??= string.Empty;
        var builder = ImmutableArray.CreateBuilder<TerminalTextElement>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var cell = 0;
        while (enumerator.MoveNext())
        {
            var value = (string)enumerator.Current;
            var width = ElementWidth(value);
            builder.Add(new TerminalTextElement(value, enumerator.ElementIndex, value.Length, cell, width));
            cell += width;
        }

        return builder.ToImmutable();
    }

    public static string SliceByCells(string text, int startCell, int endCellExclusive)
    {
        if (string.IsNullOrEmpty(text) || endCellExclusive <= startCell)
        {
            return string.Empty;
        }

        var start = Math.Max(0, startCell);
        var end = Math.Max(start, endCellExclusive);
        var builder = new StringBuilder();
        foreach (var element in Enumerate(text))
        {
            var elementEnd = element.CellStart + Math.Max(1, element.CellWidth);
            if (elementEnd <= start || element.CellStart >= end)
            {
                continue;
            }

            builder.Append(element.Text);
        }

        return builder.ToString();
    }

    public static ImmutableArray<WrappedCellRow> Wrap(string text, int width)
    {
        text ??= string.Empty;
        var safeWidth = Math.Max(1, width);
        var rows = ImmutableArray.CreateBuilder<WrappedCellRow>();
        var logicalStart = 0;

        while (logicalStart <= text.Length)
        {
            var newline = text.IndexOf('\n', logicalStart);
            var logicalEnd = newline < 0 ? text.Length : newline;
            AppendLogicalLine(rows, text, logicalStart, logicalEnd, safeWidth);
            if (newline < 0)
            {
                break;
            }

            logicalStart = newline + 1;
            if (logicalStart == text.Length)
            {
                rows.Add(new WrappedCellRow(string.Empty, logicalStart, logicalStart, 0, []));
                break;
            }
        }

        return rows.Count == 0
            ? [new WrappedCellRow(string.Empty, 0, 0, 0, [])]
            : rows.ToImmutable();
    }

    private static void AppendLogicalLine(
        ImmutableArray<WrappedCellRow>.Builder rows,
        string source,
        int start,
        int end,
        int width)
    {
        if (start == end)
        {
            rows.Add(new WrappedCellRow(string.Empty, start, end, 0, []));
            return;
        }

        var line = source[start..end];
        var elements = Enumerate(line);
        var rowStart = 0;
        while (rowStart < elements.Length)
        {
            var used = 0;
            var cursor = rowStart;
            var lastWhitespace = -1;
            while (cursor < elements.Length)
            {
                var next = elements[cursor];
                var nextWidth = Math.Max(1, next.CellWidth);
                if (used > 0 && used + nextWidth > width)
                {
                    break;
                }

                used += nextWidth;
                if (string.IsNullOrWhiteSpace(next.Text))
                {
                    lastWhitespace = cursor;
                }

                cursor++;
                if (used >= width)
                {
                    break;
                }
            }

            var rowEnd = cursor;
            if (cursor < elements.Length && lastWhitespace >= rowStart)
            {
                rowEnd = lastWhitespace;
                cursor = lastWhitespace + 1;
                while (cursor < elements.Length && string.IsNullOrWhiteSpace(elements[cursor].Text))
                {
                    cursor++;
                }
            }

            if (rowEnd <= rowStart)
            {
                rowEnd = Math.Min(elements.Length, rowStart + 1);
                cursor = rowEnd;
            }

            var rowElements = elements[rowStart..rowEnd];
            var rowText = string.Concat(rowElements.Select(element => element.Text));
            var absoluteStart = start + rowElements[0].Utf16Start;
            var last = rowElements[^1];
            var absoluteEnd = start + last.Utf16Start + last.Utf16Length;
            var normalized = rowElements
                .Select(element => element with
                {
                    Utf16Start = start + element.Utf16Start,
                    CellStart = element.CellStart - rowElements[0].CellStart,
                })
                .ToImmutableArray();
            rows.Add(new WrappedCellRow(rowText, absoluteStart, absoluteEnd, Width(rowText), normalized));
            rowStart = cursor;
        }
    }

    private static int ElementWidth(string element)
    {
        var width = 0;
        foreach (var rune in element.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            {
                continue;
            }

            width = Math.Max(width, IsWide(rune.Value) ? 2 : 1);
        }

        return width;
    }

    private static bool IsWide(int codePoint) =>
        (codePoint >= 0x1100 && codePoint <= 0x115F) ||
        (codePoint >= 0x2E80 && codePoint <= 0x303E) ||
        (codePoint >= 0x3041 && codePoint <= 0x33FF) ||
        (codePoint >= 0x3400 && codePoint <= 0x4DBF) ||
        (codePoint >= 0x4E00 && codePoint <= 0x9FFF) ||
        (codePoint >= 0xA000 && codePoint <= 0xA4CF) ||
        (codePoint >= 0xAC00 && codePoint <= 0xD7A3) ||
        (codePoint >= 0xF900 && codePoint <= 0xFAFF) ||
        (codePoint >= 0xFF00 && codePoint <= 0xFF60) ||
        (codePoint >= 0xFFE0 && codePoint <= 0xFFE6) ||
        (codePoint >= 0x1F300 && codePoint <= 0x1FAFF) ||
        (codePoint >= 0x20000 && codePoint <= 0x3FFFD);
}
```

In `TranscriptBlockFormatter.cs`, replace all calls to its private `DisplayWidth` with `TerminalCellText.Width`, replace `BreakWord` grapheme iteration with `TerminalCellText.Enumerate`, and delete private `DisplayWidth`, `RuneWidth`, and `IsWide`. Keep the formatter's existing word/preformatted semantics and test expectations unchanged.

- [ ] **Step 4: Run the focused tests and verify green**

Run the Step 2 command again.

Expected: PASS; wide CJK occupies two cells, combining marks stay attached, and existing formatter tests remain green.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Rendering\TerminalCellText.cs src\Coda.Tui\Ui\Rendering\TranscriptBlockFormatter.cs tests\Coda.Tui.Tests\TerminalCellTextTests.cs tests\Coda.Tui.Tests\TranscriptBlockFormatterTests.cs
git commit -m "refactor(tui): centralize terminal cell layout" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: Add the Warm Ember semantic theme and migrate retained views

**Files:**
- Create: `src/Coda.Tui/Ui/Rendering/TuiTheme.cs`
- Create: `tests/Coda.Tui.Tests/TuiThemeTests.cs`
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs:343-362`
- Modify: `src/Coda.Tui/Ui/Shells/ComposerChromeView.cs`
- Modify: `src/Coda.Tui/Ui/Shells/CommandCompletionView.cs:90-119,152-181`
- Modify: `src/Coda.Tui/Ui/Shells/PromptOverlay.cs:33-43`
- Modify: `tests/Coda.Tui.Tests/ComposerChromeViewTests.cs`
- Modify: `tests/Coda.Tui.Tests/CommandCompletionViewTests.cs`

- [ ] **Step 1: Write failing palette and fallback tests**

```csharp
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using TgColor = Terminal.Gui.Drawing.Color;
using TgName = Terminal.Gui.Drawing.ColorName16;

namespace Coda.Tui.Tests;

public sealed class TuiThemeTests
{
    [Fact]
    public void Warm_ember_exposes_exact_semantic_rgb_and_named_fallbacks()
    {
        var theme = TuiTheme.WarmEmber;

        Assert.Equal(new TgColor(242, 214, 179), theme.TranscriptAssistant.TrueColor);
        Assert.Equal(new TgColor(230, 168, 74), theme.TranscriptUser.TrueColor);
        Assert.Equal(new TgColor(215, 168, 75), theme.TranscriptTool.TrueColor);
        Assert.Equal(new TgColor(233, 130, 107), theme.PermissionApproval.TrueColor);
        Assert.Equal(new TgColor(240, 199, 94), theme.Question.TrueColor);
        Assert.Equal(new TgColor(217, 104, 93), theme.Error.TrueColor);

        Assert.Equal(TgName.White, theme.TranscriptAssistant.Fallback);
        Assert.Equal(TgName.BrightYellow, theme.TranscriptUser.Fallback);
        Assert.Equal(TgName.BrightYellow, theme.TranscriptTool.Fallback);
        Assert.Equal(TgName.BrightRed, theme.PermissionApproval.Fallback);
        Assert.Equal(TgName.BrightYellow, theme.Question.Fallback);
        Assert.Equal(TgName.Red, theme.Error.Fallback);
        Assert.NotEqual(TgName.Blue, theme.TranscriptTool.Fallback);
        Assert.NotEqual(TgName.Magenta, theme.PermissionApproval.Fallback);
    }

    [Fact]
    public void Resolve_uses_rgb_for_truecolor_and_named_value_for_low_color()
    {
        var role = TuiTheme.WarmEmber.TranscriptTool;

        Assert.Equal(role.TrueColor, TuiTheme.Resolve(role, trueColor: true));
        Assert.Equal(new TgColor(role.Fallback), TuiTheme.Resolve(role, trueColor: false));
    }

    [Theory]
    [InlineData(TranscriptRole.Assistant, 242, 214, 179)]
    [InlineData(TranscriptRole.User, 230, 168, 74)]
    [InlineData(TranscriptRole.Tool, 215, 168, 75)]
    [InlineData(TranscriptRole.Permission, 233, 130, 107)]
    [InlineData(TranscriptRole.Question, 240, 199, 94)]
    [InlineData(TranscriptRole.Warning, 240, 199, 94)]
    [InlineData(TranscriptRole.Error, 217, 104, 93)]
    public void Transcript_roles_resolve_through_theme(
        TranscriptRole role,
        int red,
        int green,
        int blue)
    {
        using IApplication app = Application.Create();
        using var view = new VirtualizedTranscriptView(app, theme: TuiTheme.WarmEmber);

        var color = view.AttributeFor(role, trueColor: true).Foreground;

        Assert.Equal(new TgColor(red, green, blue), color);
    }

    [Fact]
    public void Forced_16_color_driver_uses_named_tool_and_approval_fallbacks()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.Force16Colors = true;
        using var view = new VirtualizedTranscriptView(app, theme: TuiTheme.WarmEmber);

        Assert.Equal(
            new TgColor(TgName.BrightYellow),
            view.AttributeFor(TranscriptRole.Tool).Foreground);
        Assert.Equal(
            new TgColor(TgName.BrightRed),
            view.AttributeFor(TranscriptRole.Permission).Foreground);
    }
}
```

Replace the old chrome accent/startup tests with:

```csharp
[Fact]
public void Ready_state_is_dark_borderless_and_draws_only_the_warm_prompt()
{
    using var chrome = new ComposerChromeView(TuiTheme.WarmEmber);

    var rows = chrome.RenderRows(width: 12, height: 3);

    Assert.Equal([">           ", "            ", "            "], rows);
    Assert.DoesNotContain(rows, row => row.Contains('▌'));
    Assert.DoesNotContain(rows, row => row.Contains("Initializing", StringComparison.Ordinal));
}

[Fact]
public void Startup_state_keeps_the_dark_region_but_draws_no_chrome_text()
{
    using var chrome = new ComposerChromeView(TuiTheme.WarmEmber);

    chrome.SetReady(false);

    Assert.Equal(string.Empty, chrome.DisplayText);
    Assert.Equal(["            ", "            ", "            "], chrome.RenderRows(12, 3));
}
```

Add to `CommandCompletionViewTests.cs`:

```csharp
[Fact]
public void Completion_attributes_use_warm_ember_normal_and_selected_roles()
{
    using var view = new CommandCompletionView(TuiTheme.WarmEmber);

    var normal = view.AttributeFor(selected: false, trueColor: true);
    var selected = view.AttributeFor(selected: true, trueColor: true);

    Assert.Equal(TuiTheme.WarmEmber.CompletionNormal.TrueColor, normal.Foreground);
    Assert.Equal(TuiTheme.WarmEmber.CompletionSelectedBackground.TrueColor, selected.Background);
    Assert.NotEqual(normal, selected);
}
```

- [ ] **Step 2: Run the focused tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiThemeTests|FullyQualifiedName~ComposerChromeViewTests|FullyQualifiedName~CommandCompletionViewTests" --no-restore
```

Expected: FAIL to compile because `TuiTheme` and themed constructors/attributes do not exist; existing chrome tests also fail because they still observe the accent bar and `Initializing…`.

- [ ] **Step 3: Implement the exact semantic palette**

Create `TuiTheme.cs` with one immutable `WarmEmber` instance and these values:

```csharp
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;
using TgName = Terminal.Gui.Drawing.ColorName16;
using TgScheme = Terminal.Gui.Drawing.Scheme;

namespace Coda.Tui.Ui.Rendering;

internal readonly record struct TuiThemeColor(TgColor TrueColor, TgName Fallback);

internal sealed class TuiTheme
{
    public static TuiTheme WarmEmber { get; } = new();

    private TuiTheme()
    {
    }

    public TuiThemeColor Background { get; } = new(new TgColor(23, 19, 16), TgName.Black);
    public TuiThemeColor TranscriptAssistant { get; } = new(new TgColor(242, 214, 179), TgName.White);
    public TuiThemeColor TranscriptUser { get; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);
    public TuiThemeColor Heading { get; } = new(new TgColor(240, 179, 91), TgName.BrightYellow);
    public TuiThemeColor Code { get; } = new(new TgColor(200, 184, 166), TgName.Gray);
    public TuiThemeColor TranscriptTool { get; } = new(new TgColor(215, 168, 75), TgName.BrightYellow);
    public TuiThemeColor Diff { get; } = new(new TgColor(201, 138, 82), TgName.Yellow);
    public TuiThemeColor PermissionApproval { get; } = new(new TgColor(233, 130, 107), TgName.BrightRed);
    public TuiThemeColor Question { get; } = new(new TgColor(240, 199, 94), TgName.BrightYellow);
    public TuiThemeColor Warning { get; } = new(new TgColor(240, 199, 94), TgName.Yellow);
    public TuiThemeColor Notification { get; } = new(new TgColor(191, 174, 156), TgName.Gray);
    public TuiThemeColor Error { get; } = new(new TgColor(217, 104, 93), TgName.Red);

    public TuiThemeColor ComposerText { get; } = new(new TgColor(242, 214, 179), TgName.White);
    public TuiThemeColor ComposerPrompt { get; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);

    public TuiThemeColor OperationalReady { get; } = new(new TgColor(143, 136, 128), TgName.Gray);
    public TuiThemeColor OperationalInitializing { get; } = new(new TgColor(179, 138, 80), TgName.Yellow);
    public TuiThemeColor OperationalWorking { get; } = new(new TgColor(229, 139, 54), TgName.BrightYellow);
    public TuiThemeColor OperationalThinking { get; } = new(new TgColor(216, 94, 94), TgName.BrightRed);
    public TuiThemeColor OperationalWaiting { get; } = new(new TgColor(143, 136, 128), TgName.Gray);

    public TuiThemeColor CompletionNormal { get; } = new(new TgColor(215, 194, 168), TgName.White);
    public TuiThemeColor CompletionSelectedText { get; } = new(new TgColor(23, 19, 16), TgName.Black);
    public TuiThemeColor CompletionSelectedBackground { get; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);

    public TuiThemeColor PromptText { get; } = new(new TgColor(242, 214, 179), TgName.White);
    public TuiThemeColor PromptAccent { get; } = new(new TgColor(233, 130, 107), TgName.BrightRed);
    public TuiThemeColor SelectionText { get; } = new(new TgColor(23, 19, 16), TgName.Black);
    public TuiThemeColor SelectionBackground { get; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);

    public static TgColor Resolve(TuiThemeColor role, bool trueColor) =>
        trueColor ? role.TrueColor : new TgColor(role.Fallback);

    public static bool SupportsTrueColor(IDriver? driver) =>
        driver is { SupportsTrueColor: true, Force16Colors: false };

    public TgAttribute Attribute(TuiThemeColor foreground, TuiThemeColor background, IDriver? driver) =>
        new(Resolve(foreground, SupportsTrueColor(driver)), Resolve(background, SupportsTrueColor(driver)));

    public TgScheme ComposerScheme(IDriver? driver)
    {
        var normal = this.Attribute(this.ComposerText, this.Background, driver);
        var focus = this.Attribute(this.TranscriptAssistant, this.Background, driver);
        return SolidScheme(normal, focus);
    }

    public TgScheme PromptScheme(IDriver? driver)
    {
        var normal = this.Attribute(this.PromptText, this.Background, driver);
        var focus = this.Attribute(this.PromptAccent, this.Background, driver);
        return SolidScheme(normal, focus);
    }

    private static TgScheme SolidScheme(TgAttribute normal, TgAttribute focus) => new()
    {
        Normal = normal,
        HotNormal = normal,
        Focus = focus,
        HotFocus = focus,
        Active = focus,
        HotActive = focus,
        Highlight = focus,
        Editable = normal,
        ReadOnly = normal,
        Disabled = normal,
    };
}
```

- [ ] **Step 4: Migrate all currently retained themed surfaces**

Apply these exact seams:

```csharp
// VirtualizedTranscriptView
private readonly TuiTheme theme;

public VirtualizedTranscriptView(
    IApplication app,
    Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>>? formatter = null,
    TuiTheme? theme = null)
{
    this.app = app ?? throw new ArgumentNullException(nameof(app));
    this.theme = theme ?? TuiTheme.WarmEmber;
    this.index = new TranscriptLayoutIndex(formatter ?? TranscriptBlockFormatter.Format);
    this.CanFocus = true;
}

internal TgAttribute AttributeFor(TranscriptRole role, bool? trueColor = null)
{
    var foreground = role switch
    {
        TranscriptRole.User => this.theme.TranscriptUser,
        TranscriptRole.Heading => this.theme.Heading,
        TranscriptRole.Code => this.theme.Code,
        TranscriptRole.Tool => this.theme.TranscriptTool,
        TranscriptRole.Diff => this.theme.Diff,
        TranscriptRole.Permission => this.theme.PermissionApproval,
        TranscriptRole.Question => this.theme.Question,
        TranscriptRole.Warning => this.theme.Warning,
        TranscriptRole.Notification => this.theme.Notification,
        TranscriptRole.Error => this.theme.Error,
        _ => this.theme.TranscriptAssistant,
    };
    var useTrueColor = trueColor ?? TuiTheme.SupportsTrueColor(this.app.Driver);
    return new TgAttribute(
        TuiTheme.Resolve(foreground, useTrueColor),
        TuiTheme.Resolve(this.theme.Background, useTrueColor));
}
```

```csharp
// ComposerChromeView
internal const string PromptGlyph = ">";
private const int PromptColumn = 0;
private readonly TuiTheme theme;
private bool ready = true;

public ComposerChromeView(TuiTheme? theme = null)
{
    this.theme = theme ?? TuiTheme.WarmEmber;
    this.CanFocus = false;
}

internal string DisplayText => this.ready ? PromptGlyph : string.Empty;
internal TgScheme CreateInputScheme(IDriver? driver) => this.theme.ComposerScheme(driver);
```

`RenderRows` must fill every row with spaces, put `>` only at `[0]` on row zero when ready, and never emit `▌` or `Initializing…`. `OnDrawingContent` must paint the whole viewport with `theme.Background`, then paint only the ready `>` using `theme.ComposerPrompt`.

```csharp
// CommandCompletionView
private readonly TuiTheme theme;

public CommandCompletionView(TuiTheme? theme = null)
{
    this.theme = theme ?? TuiTheme.WarmEmber;
    this.CanFocus = false;
    this.Visible = false;
}

internal TgAttribute AttributeFor(bool selected, bool? trueColor = null)
{
    var useTrueColor = trueColor ?? TuiTheme.SupportsTrueColor(this.App?.Driver);
    return selected
        ? new TgAttribute(
            TuiTheme.Resolve(this.theme.CompletionSelectedText, useTrueColor),
            TuiTheme.Resolve(this.theme.CompletionSelectedBackground, useTrueColor))
        : new TgAttribute(
            TuiTheme.Resolve(this.theme.CompletionNormal, useTrueColor),
            TuiTheme.Resolve(this.theme.Background, useTrueColor));
}
```

Replace `Fit` with `TerminalCellText.SliceByCells(flattened, 0, width)`. Use `AttributeFor(index == selectedIndex)` in drawing.

```csharp
// PromptOverlay constructor
public PromptOverlay(IUiEventPublisher publisher, TuiTheme? theme = null)
{
    this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    this.theme = theme ?? TuiTheme.WarmEmber;
    this.CanFocus = true;
    this.Visible = false;
    this.BorderStyle = LineStyle.Rounded;
    this.titleLabel = new Label { X = 0, Y = 0, Width = Dim.Fill(), CanFocus = false };
    this.bodyLabel = new Label
    {
        X = 0,
        Y = 2,
        Width = Dim.Fill(),
        Height = Dim.Fill(),
        CanFocus = false,
    };
    this.Add(this.titleLabel, this.bodyLabel);
}

internal void ApplyTheme(IDriver? driver) =>
    this.SetScheme(this.theme.PromptScheme(driver));
```

After constructing `PromptOverlay` in `TerminalGuiShellBase`, call
`this.PromptOverlay.ApplyTheme(app.Driver)`. Keep it as the final subview so it stays topmost.

- [ ] **Step 5: Run focused tests and verify green**

Run the Step 2 command again.

Expected: PASS. The tool fallback is bright yellow rather than blue, permission/approval is bright red/coral rather than magenta, startup chrome is blank, and no retained view owns an independent hard-coded palette.

- [ ] **Step 6: Commit**

```powershell
git add src\Coda.Tui\Ui\Rendering\TuiTheme.cs src\Coda.Tui\Ui\Shells\VirtualizedTranscriptView.cs src\Coda.Tui\Ui\Shells\ComposerChromeView.cs src\Coda.Tui\Ui\Shells\CommandCompletionView.cs src\Coda.Tui\Ui\Shells\PromptOverlay.cs tests\Coda.Tui.Tests\TuiThemeTests.cs tests\Coda.Tui.Tests\ComposerChromeViewTests.cs tests\Coda.Tui.Tests\CommandCompletionViewTests.cs
git commit -m "feat(tui): add Warm Ember semantic theme" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: Make the virtualized transcript full width

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs:9-42,65-70,225-239`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs:43-100,292-332,733-755`
- Modify: `tests/Coda.Tui.Tests/InlineTuiShellTests.cs:74-110`

- [ ] **Step 1: Replace capped/centered assertions with full-width and resize assertions**

In `FullscreenTuiShellTests.cs`, replace the `MaximumTranscriptWidth` and centering tests with:

```csharp
[Theory]
[InlineData(60, 12)]
[InlineData(80, 24)]
[InlineData(160, 40)]
public void Fullscreen_transcript_uses_all_available_width(int width, int height)
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(width, height);
    using var shell = ShellTestFactory.CreateFullscreen(app);

    var token = app.Begin(shell);
    app.LayoutAndDraw();

    Assert.Equal(0, shell.Transcript.Frame.X);
    Assert.Equal(width, shell.Transcript.Frame.Width);

    if (token is not null)
    {
        app.End(token);
    }
}

[Fact]
public async Task Fullscreen_resize_reflows_full_width_once_and_keeps_incremental_updates()
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(160, 30);
    using var shell = ShellTestFactory.CreateFullscreen(app);
    var token = app.Begin(shell);
    app.LayoutAndDraw();

    var first = new UserTranscriptBlock(Guid.NewGuid(), new string('x', 200));
    await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = [first] }, CancellationToken.None);
    app.LayoutAndDraw();
    var replaces = shell.Transcript.ReplaceAllCount;

    app.Driver.SetScreenSize(90, 30);
    app.LayoutAndDraw();
    Assert.Equal(90, shell.Transcript.Frame.Width);
    Assert.Equal(90, shell.Transcript.ActiveLayoutWidth);
    Assert.Equal(replaces, shell.Transcript.ReplaceAllCount);

    var second = new AssistantTranscriptBlock(Guid.NewGuid(), "tail", Complete: true);
    await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = [first, second] }, CancellationToken.None);
    Assert.Equal(1, shell.Transcript.AppendCount);

    if (token is not null)
    {
        app.End(token);
    }
}
```

Update the existing layout tests to assert `shell.Transcript.Frame.Width == width` for all widths. Add the equivalent `Assert.Equal(width, shell.Transcript.Frame.Width)` to the inline layout theory.

- [ ] **Step 2: Run the shell tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests" --no-restore
```

Expected: FAIL because 160-column layouts still cap the transcript at 120 and center it; `ActiveLayoutWidth` does not exist.

- [ ] **Step 3: Remove the cap without touching virtualization**

In `VirtualizedTranscriptView`, expose:

```csharp
internal int ActiveLayoutWidth => this.index.ActiveWidth;
```

In `FullscreenTuiShell.BuildLayout`, use:

```csharp
this.transcript = new VirtualizedTranscriptView(
    this.HostApp,
    transcriptFormatter,
    TuiTheme.WarmEmber);
this.transcript.TranscriptScrolled += this.RefreshHeaderForViewport;
this.transcript.X = 0;
this.transcript.Y = Pos.Bottom(this.header);
this.transcript.Width = Dim.Fill();
this.transcript.Height = Dim.Fill(4);
```

Delete `MaximumTranscriptWidth`, `TranscriptWidth`, `Pos.Center()`, and all comments describing a 120-column cap. Do not change `TranscriptLayoutIndex`, `ReplaceAll`, `Append`, `ReplaceLast`, `ReplaceAt`, overscan, or cache bounds in this task.

- [ ] **Step 4: Run shell and virtualization tests and verify green**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests|FullyQualifiedName~TranscriptLayoutIndexTests|FullyQualifiedName~VirtualizedTranscriptViewTests" --no-restore
```

Expected: PASS. Resizing changes only the active layout width/reflow; append and replace counters retain their incremental behavior.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\FullscreenTuiShell.cs src\Coda.Tui\Ui\Shells\VirtualizedTranscriptView.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs tests\Coda.Tui.Tests\InlineTuiShellTests.cs
git commit -m "feat(tui): use full transcript width" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: Split operational state from stable metadata

**Files:**
- Create: `src/Coda.Tui/Ui/State/OperationalStatus.cs`
- Create: `src/Coda.Tui/Ui/State/OperationalStatusProjector.cs`
- Create: `tests/Coda.Tui.Tests/OperationalStatusProjectorTests.cs`
- Modify: `src/Coda.Tui/Ui/State/StatusProjector.cs:45-95`
- Modify: `tests/Coda.Tui.Tests/StatusProjectorTests.cs`

- [ ] **Step 1: Write the complete projection-priority matrix**

```csharp
using Coda.Agent;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class OperationalStatusProjectorTests
{
    [Fact]
    public void Pending_approval_has_highest_priority()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            Permission = new PermissionStatus(PermissionMode.Default, 1),
            ActiveOperation = new ActiveOperation("startup", "Starting…", null),
            RunningTasks = 2,
        };

        Assert.Equal(
            new OperationalStatus("! Waiting for approval", OperationalTone.Approval, Animated: false),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Non_confirmation_prompt_waits_for_input_without_claiming_approval()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            PendingPrompt = UiPromptRequest.Select(
                "Choose model",
                [new UiPromptOption("one", "One")]),
        };

        Assert.Equal(
            new OperationalStatus("◌ Waiting for input", OperationalTone.Waiting, Animated: false),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Startup_projects_initializing()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("startup", "Starting…", null),
        };

        Assert.Equal(
            new OperationalStatus("Initializing…", OperationalTone.Initializing, Animated: true),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Incomplete_tool_outranks_active_turn()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("turn", "answer", null),
            Transcript =
            [
                new ToolTranscriptBlock(
                    Guid.NewGuid(),
                    "dotnet test",
                    "{}",
                    null,
                    null,
                    IsError: false,
                    Complete: false),
            ],
        };

        Assert.Equal(
            new OperationalStatus("Working · dotnet test", OperationalTone.Working, Animated: true),
            OperationalStatusProjector.Project(snapshot));
    }

    [Theory]
    [InlineData("high")]
    [InlineData("max")]
    public void High_and_max_active_turns_project_intensive_thinking(string effort)
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            EffectiveEffort = effort,
            ActiveOperation = new ActiveOperation("turn", "answer", null),
        };

        Assert.Equal(
            new OperationalStatus("Thinking deeply", OperationalTone.Thinking, Animated: true),
            OperationalStatusProjector.Project(snapshot));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("low")]
    [InlineData("medium")]
    public void Other_active_turns_project_working(string effort)
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            EffectiveEffort = effort,
            ActiveOperation = new ActiveOperation("turn", "running tests", null),
        };

        Assert.Equal(
            new OperationalStatus("Working · running tests", OperationalTone.Working, Animated: true),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Background_tasks_project_waiting_count()
    {
        var snapshot = UiSessionSnapshot.Empty with { RunningTasks = 2 };

        Assert.Equal(
            new OperationalStatus(
                "Waiting for 2 background tasks",
                OperationalTone.Waiting,
                Animated: false),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Idle_error_projects_a_concise_error()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            Notification = new UiNotification("Connection failed\nstack details", UiNotificationLevel.Error),
        };

        Assert.Equal(
            new OperationalStatus("Connection failed", OperationalTone.Error, Animated: false),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Idle_snapshot_is_ready()
    {
        Assert.Equal(
            new OperationalStatus("Ready", OperationalTone.Ready, Animated: false),
            OperationalStatusProjector.Project(UiSessionSnapshot.Empty));
    }
}
```

Replace the existing narrow-prefix test and add the operation regression in `StatusProjectorTests.cs`:

```csharp
[Fact]
public void Narrow_width_44_renders_only_stable_metadata_prefix()
{
    var snapshot = UiSessionSnapshot.Empty with
    {
        Model = "gpt-5.6-sol",
        EffectiveEffort = "high",
        Context = new ContextStatus(84_000, 200_000, 42, true),
        Permission = new PermissionStatus(PermissionMode.Default, 1),
        ActiveOperation = new ActiveOperation("tool", "running tool", null),
        WorkingDirectory = string.Empty,
    };

    Assert.Equal(
        "gpt-5.6-sol | high | ctx 42%",
        StatusProjector.Project(snapshot, 44));
}

[Fact]
public void Metadata_never_contains_active_operation_label()
{
    var snapshot = Wide() with
    {
        ActiveOperation = new ActiveOperation("tool", "running secret tool label", null),
    };

    var line = StatusProjector.Project(snapshot, width: 200);

    Assert.DoesNotContain("running secret tool label", line, StringComparison.Ordinal);
    Assert.DoesNotContain("permission", line, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("default", line, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("gpt-5.6-sol", line, StringComparison.Ordinal);
    Assert.Contains("ctx 84k/200k", line, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run projector tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~OperationalStatusProjectorTests|FullyQualifiedName~StatusProjectorTests" --no-restore
```

Expected: FAIL to compile because the operational types/projector do not exist; metadata regression fails because `StatusProjector` still adds `ActiveOperation.Label`.

- [ ] **Step 3: Add the immutable display model and pure projector**

Create `OperationalStatus.cs`:

```csharp
namespace Coda.Tui.Ui.State;

internal enum OperationalTone
{
    Ready,
    Initializing,
    Working,
    Thinking,
    Waiting,
    Approval,
    Warning,
    Error,
}

internal sealed record OperationalStatus(string Text, OperationalTone Tone, bool Animated);
```

Create `OperationalStatusProjector.cs`:

```csharp
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Ui.State;

internal static class OperationalStatusProjector
{
    public static OperationalStatus Project(UiSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Permission.PendingCount > 0 ||
            snapshot.PendingPrompt is { Kind: UiPromptKind.Confirm })
        {
            return new("! Waiting for approval", OperationalTone.Approval, false);
        }

        if (snapshot.PendingPrompt is not null)
        {
            return new("◌ Waiting for input", OperationalTone.Waiting, false);
        }

        if (snapshot.ActiveOperation is { Kind: "startup" })
        {
            return new("Initializing…", OperationalTone.Initializing, true);
        }

        var tool = LastIncompleteTool(snapshot);
        if (tool is not null)
        {
            return new($"Working · {tool.ToolName}", OperationalTone.Working, true);
        }

        if (snapshot.ActiveOperation is { } operation)
        {
            if (operation.Kind == "turn" &&
                snapshot.EffectiveEffort is "high" or "max")
            {
                return new("Thinking deeply", OperationalTone.Thinking, true);
            }

            var label = string.IsNullOrWhiteSpace(operation.Label)
                ? "Working"
                : $"Working · {SingleLine(operation.Label)}";
            return new(label, OperationalTone.Working, true);
        }

        if (snapshot.RunningTasks > 0)
        {
            var text = snapshot.RunningTasks == 1
                ? "Waiting for 1 background task"
                : $"Waiting for {snapshot.RunningTasks} background tasks";
            return new(text, OperationalTone.Waiting, false);
        }

        if (snapshot.Notification is { Level: UiNotificationLevel.Error } error)
        {
            return new(SingleLine(error.Message), OperationalTone.Error, false);
        }

        return new("Ready", OperationalTone.Ready, false);
    }

    private static ToolTranscriptBlock? LastIncompleteTool(UiSessionSnapshot snapshot)
    {
        for (var index = snapshot.Transcript.Length - 1; index >= 0; index--)
        {
            if (snapshot.Transcript[index] is ToolTranscriptBlock { Complete: false } tool)
            {
                return tool;
            }
        }

        return null;
    }

    private static string SingleLine(string value)
    {
        var newline = value.IndexOfAny(['\r', '\n']);
        return (newline < 0 ? value : value[..newline]).Trim();
    }
}
```

In `StatusProjector.BuildFields`, delete both changing permission/operation fields:

```csharp
fields.Add(FormatPermission(snapshot.Permission));

if (snapshot.ActiveOperation is { } operation)
{
    fields.Add(operation.Label);
}
```

Delete now-unused `FormatPermission`. Renumber comments so model, effort, context, token usage, cost, MCP, LSP, git, and cwd remain in that exact priority order.

- [ ] **Step 4: Run projector tests and verify green**

Run the Step 2 command again.

Expected: PASS for every state/tone/animated combination and for metadata excluding active-operation text.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\State\OperationalStatus.cs src\Coda.Tui\Ui\State\OperationalStatusProjector.cs src\Coda.Tui\Ui\State\StatusProjector.cs tests\Coda.Tui.Tests\OperationalStatusProjectorTests.cs tests\Coda.Tui.Tests\StatusProjectorTests.cs
git commit -m "feat(tui): project dedicated operational status" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: Add the operational view and final retained-shell row order

**Files:**
- Create: `src/Coda.Tui/Ui/Shells/OperationalStatusView.cs`
- Create: `tests/Coda.Tui.Tests/OperationalStatusViewTests.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Modify: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs`
- Modify: `src/Coda.Tui/Ui/Shells/InlineTuiShell.cs`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/InlineTuiShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/CommandCompletionShellTests.cs`

- [ ] **Step 1: Write failing status-view timer and row-order tests**

Create `OperationalStatusViewTests.cs`:

```csharp
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class OperationalStatusViewTests
{
    [Fact]
    public void Static_status_never_starts_a_timer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var view = new OperationalStatusView(app, TuiTheme.WarmEmber);

        view.SetStatus(new OperationalStatus("Ready", OperationalTone.Ready, false));

        Assert.False(view.TimerActive);
        Assert.Equal("· Ready", view.RenderText());
    }

    [Fact]
    public void Animated_status_ticks_only_this_view_and_stops_on_static_state()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        Func<bool>? callback = null;
        var removed = 0;
        using var view = new OperationalStatusView(
            app,
            TuiTheme.WarmEmber,
            addTimeout: (_, next) =>
            {
                callback = next;
                return new object();
            },
            removeTimeout: _ =>
            {
                removed++;
                return true;
            });

        view.SetStatus(new OperationalStatus("Working", OperationalTone.Working, true));
        var before = view.SpinnerFrame;
        Assert.True(view.TimerActive);

        Assert.True(callback!());
        Assert.NotEqual(before, view.SpinnerFrame);
        Assert.Equal(1, view.AnimationDrawRequests);

        view.SetStatus(new OperationalStatus("Ready", OperationalTone.Ready, false));
        Assert.False(view.TimerActive);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Dispose_removes_an_active_timer_and_callback_stops()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        Func<bool>? callback = null;
        var removed = 0;
        var view = new OperationalStatusView(
            app,
            TuiTheme.WarmEmber,
            addTimeout: (_, next) =>
            {
                callback = next;
                return new object();
            },
            removeTimeout: _ =>
            {
                removed++;
                return true;
            });
        view.SetStatus(new OperationalStatus("Working", OperationalTone.Working, true));

        view.Dispose();

        Assert.Equal(1, removed);
        Assert.False(callback!());
    }
}
```

Replace retained row-order assertions with:

```csharp
Assert.Equal(0, shell.Header.Frame.Y);
Assert.Equal(shell.Header.Frame.Bottom, shell.Transcript.Frame.Y);
Assert.Equal(shell.Operational.Frame.Y, shell.Transcript.Frame.Bottom);
Assert.Equal(shell.Composer.Frame.Y, shell.Operational.Frame.Bottom);
Assert.Equal(shell.Status.Frame.Y, shell.Composer.Frame.Bottom);
Assert.Equal(shell.Frame.Bottom, shell.Status.Frame.Bottom);
Assert.Equal(1, shell.Operational.Frame.Height);
Assert.Equal(1, shell.Status.Frame.Height);
```

Update completion geometry assertions in both modes:

```csharp
Assert.Equal(shell.Operational.Frame.Y, shell.Completion.Frame.Bottom);
Assert.Equal(composerY, shell.Composer.Frame.Y);
Assert.Equal(operationalY, shell.Operational.Frame.Y);
Assert.Equal(statusY, shell.Status.Frame.Y);
```

Update startup assertions:

```csharp
Assert.False(shell.Composer.Visible);
Assert.False(shell.Chrome.Ready);
Assert.Equal(string.Empty, shell.Chrome.DisplayText);
Assert.Equal("Initializing…", shell.Operational.Status.Text);
Assert.DoesNotContain("Initializing", string.Join('\n', shell.Chrome.RenderRows(80, 3)));
```

- [ ] **Step 2: Run focused view/layout tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~OperationalStatusViewTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests|FullyQualifiedName~CommandCompletionShellTests" --no-restore
```

Expected: FAIL to compile because `OperationalStatusView` and `shell.Operational` do not exist; existing status remains below the composer and completion still anchors to the composer.

- [ ] **Step 3: Implement the one-row themed operational view**

Create `OperationalStatusView.cs`:

```csharp
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace Coda.Tui.Ui.Shells;

internal sealed class OperationalStatusView : View
{
    private static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(180);

    private readonly IApplication app;
    private readonly TuiTheme theme;
    private readonly Func<TimeSpan, Func<bool>, object> addTimeout;
    private readonly Func<object, bool> removeTimeout;
    private object? timer;
    private bool disposed;

    public OperationalStatusView(
        IApplication app,
        TuiTheme? theme = null,
        Func<TimeSpan, Func<bool>, object>? addTimeout = null,
        Func<object, bool>? removeTimeout = null)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.theme = theme ?? TuiTheme.WarmEmber;
        this.addTimeout = addTimeout ?? app.AddTimeout;
        this.removeTimeout = removeTimeout ?? app.RemoveTimeout;
        this.Status = new OperationalStatus("Ready", OperationalTone.Ready, false);
        this.CanFocus = false;
        this.Height = 1;
    }

    internal OperationalStatus Status { get; private set; }
    internal int SpinnerFrame { get; private set; }
    internal bool TimerActive => this.timer is not null;
    internal int AnimationDrawRequests { get; private set; }

    internal void SetStatus(OperationalStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (this.Status == status)
        {
            return;
        }

        this.StopTimer();
        this.Status = status;
        this.SpinnerFrame = 0;
        if (status.Animated)
        {
            this.timer = this.addTimeout(Interval, this.OnTick);
        }

        this.SetNeedsDraw();
    }

    internal string RenderText()
    {
        var prefix = this.Status.Animated
            ? Spinner[this.SpinnerFrame % Spinner.Length]
            : this.Status.Tone switch
            {
                OperationalTone.Ready => "·",
                OperationalTone.Approval => "!",
                OperationalTone.Error => "!",
                _ => "◌",
            };
        return $"{prefix} {this.Status.Text}";
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (context is not null)
        {
            this.ClearViewport(context);
        }

        this.SetAttribute(this.AttributeFor(this.Status.Tone));
        this.Move(0, 0);
        this.AddStr(TerminalCellText.SliceByCells(this.RenderText(), 0, Math.Max(0, this.Viewport.Width)));
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.disposed)
        {
            this.disposed = true;
            this.StopTimer();
        }

        base.Dispose(disposing);
    }

    private bool OnTick()
    {
        if (this.disposed || !this.app.Initialized || !this.Status.Animated)
        {
            this.timer = null;
            return false;
        }

        this.SpinnerFrame = (this.SpinnerFrame + 1) % Spinner.Length;
        this.AnimationDrawRequests++;
        this.SetNeedsDraw();
        return true;
    }

    private void StopTimer()
    {
        if (this.timer is not { } token)
        {
            return;
        }

        this.timer = null;
        this.removeTimeout(token);
    }

    private TgAttribute AttributeFor(OperationalTone tone)
    {
        var foreground = tone switch
        {
            OperationalTone.Initializing => this.theme.OperationalInitializing,
            OperationalTone.Working => this.theme.OperationalWorking,
            OperationalTone.Thinking => this.theme.OperationalThinking,
            OperationalTone.Waiting => this.theme.OperationalWaiting,
            OperationalTone.Approval => this.theme.PermissionApproval,
            OperationalTone.Warning => this.theme.Warning,
            OperationalTone.Error => this.theme.Error,
            _ => this.theme.OperationalReady,
        };
        return this.theme.Attribute(foreground, this.theme.Background, this.app.Driver);
    }
}
```

- [ ] **Step 4: Install the operational row and final startup chrome geometry**

Give `TerminalGuiShellBase` these final constructor parameters now so later chord/clipboard tasks do not churn signatures:

```csharp
protected TerminalGuiShellBase(
    IApplication app,
    ComposerController controller,
    IUiEventPublisher publisher,
    UiSessionSnapshot initialSnapshot,
    Func<bool>? hasActiveWork = null,
    TimeProvider? timeProvider = null,
    Func<string, bool>? clipboardWriter = null,
    Func<TimeSpan, Func<bool>, object>? addTimeout = null,
    Func<object, bool>? removeTimeout = null,
    TuiTheme? theme = null,
    Func<UiSessionSnapshot, int, string>? statusProjection = null)
```

Store/expose:

```csharp
protected TuiTheme Theme { get; }
internal OperationalStatusView Operational { get; }
internal Label Status { get; } // stable metadata only
```

Construct all views from the same theme:

```csharp
this.Theme = theme ?? TuiTheme.WarmEmber;
this.Composer = new ComposerView(controller);
this.Chrome = new ComposerChromeView(this.Theme);
this.Operational = new OperationalStatusView(
    app,
    this.Theme,
    addTimeout,
    removeTimeout);
this.Status = new Label { CanFocus = false };
this.PromptOverlay = new PromptOverlay(publisher, this.Theme);
this.PromptOverlay.ApplyTheme(app.Driver);
this.Completion = new CommandCompletionView(this.Theme);
```

In snapshot application, call:

```csharp
this.UpdateMetadata(snapshot);
this.UpdateProjectedOperationalStatus(snapshot);
this.UpdateComposerAvailability(snapshot);
this.UpdatePrompt(snapshot);
this.ApplyTranscriptChanges(previous, snapshot);
```

`UpdateProjectedOperationalStatus` calls `OperationalStatusProjector.Project(snapshot)` unless a later shell-local override is active. During startup, `UpdateComposerAvailability` must set `Chrome.SetReady(false)`, hide/disable the composer and completion, and never put initialization text in chrome.

Use these initial/final row anchors in `FullscreenTuiShell`:

```csharp
internal const int MinimumComposerHeight = 3;
internal const int ComposerGutterWidth = 2;
private int composerHeight = MinimumComposerHeight;

this.transcript.Height = Dim.Fill(this.composerHeight + 2);

this.Operational.X = 0;
this.Operational.Y = Pos.AnchorEnd(this.composerHeight + 2);
this.Operational.Width = Dim.Fill();
this.Operational.Height = 1;

this.Chrome.X = 0;
this.Chrome.Y = Pos.AnchorEnd(this.composerHeight + 1);
this.Chrome.Width = Dim.Fill();
this.Chrome.Height = this.composerHeight;

this.Composer.X = ComposerGutterWidth;
this.Composer.Y = Pos.AnchorEnd(this.composerHeight + 1);
this.Composer.Width = Dim.Fill();
this.Composer.Height = this.composerHeight;
this.Composer.BorderStyle = null;
this.Composer.SetScheme(this.Chrome.CreateInputScheme(this.HostApp.Driver));

this.Status.X = 0;
this.Status.Y = Pos.AnchorEnd(1);
this.Status.Width = Dim.Fill();
this.Status.Height = 1;
```

Place completion immediately above operational status:

```csharp
protected override void PlaceCompletion(int height, bool visible)
{
    this.Completion.Y = Pos.AnchorEnd(this.composerHeight + height + 2);
    this.Completion.Height = height;
}
```

Add views in this z-order:

```csharp
this.Add(
    this.header,
    this.transcript,
    this.Chrome,
    this.Composer,
    this.Operational,
    this.Status,
    this.Completion,
    this.PromptOverlay);
```

Set `InlineTuiShell.MinimumInlineHeight` to `6` (header + operational + minimum composer + metadata), without changing the 60×12 capability policy.

- [ ] **Step 5: Run focused tests and verify green**

Run the Step 2 command again.

Expected: PASS. Operational status is always one row immediately above the composer, metadata remains last, completion ends at the operational row, startup chrome is blank, and spinner callbacks affect only `OperationalStatusView`.

- [ ] **Step 6: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\OperationalStatusView.cs src\Coda.Tui\Ui\Shells\TerminalGuiShellBase.cs src\Coda.Tui\Ui\Shells\FullscreenTuiShell.cs src\Coda.Tui\Ui\Shells\InlineTuiShell.cs tests\Coda.Tui.Tests\OperationalStatusViewTests.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs tests\Coda.Tui.Tests\InlineTuiShellTests.cs tests\Coda.Tui.Tests\CommandCompletionShellTests.cs
git commit -m "feat(tui): add operational status row" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: Build a grapheme-aware visual composer layout map

**Files:**
- Create: `src/Coda.Tui/Ui/Input/ComposerVisualLayout.cs`
- Create: `tests/Coda.Tui.Tests/ComposerVisualLayoutTests.cs`

- [ ] **Step 1: Write pure wrap/caret/navigation tests**

```csharp
using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

public sealed class ComposerVisualLayoutTests
{
    [Fact]
    public void Explicit_and_visual_wraps_produce_stable_rows()
    {
        var layout = ComposerVisualLayout.Create("alpha beta\n界界界", width: 6);

        Assert.Equal(3, layout.VisualLineCount);
        Assert.Equal(new ComposerVisualPosition(0, 5), layout.PositionForIndex(5));
        Assert.Equal(new ComposerVisualPosition(1, 4), layout.PositionForIndex(10));
        Assert.Equal(new ComposerVisualPosition(2, 6), layout.PositionForIndex(14));
    }

    [Fact]
    public void Mapping_round_trips_utf16_indices_at_grapheme_boundaries()
    {
        const string text = "a\U0001F600e\u0301界z";
        var layout = ComposerVisualLayout.Create(text, width: 4);

        foreach (var index in new[] { 0, 1, 3, 5, 6, 7 })
        {
            var position = layout.PositionForIndex(index);
            Assert.Equal(index, layout.IndexForPosition(position.Row, position.Column));
        }
    }

    [Fact]
    public void Vertical_movement_preserves_preferred_display_column()
    {
        var layout = ComposerVisualLayout.Create("12345\n12\n123456", width: 20);

        var down = layout.MoveVertical(4, delta: 1, preferredColumn: null);
        Assert.Equal(8, down.CursorIndex);
        Assert.Equal(4, down.PreferredColumn);

        var downAgain = layout.MoveVertical(down.CursorIndex, delta: 1, down.PreferredColumn);
        Assert.Equal(13, downAgain.CursorIndex);
        Assert.Equal(4, downAgain.PreferredColumn);
    }

    [Fact]
    public void Vertical_movement_uses_visual_wrapped_rows_not_only_newlines()
    {
        var layout = ComposerVisualLayout.Create("abcdefghij", width: 4);

        var down = layout.MoveVertical(2, delta: 1, preferredColumn: null);
        var downAgain = layout.MoveVertical(down.CursorIndex, delta: 1, down.PreferredColumn);

        Assert.Equal(6, down.CursorIndex);
        Assert.Equal(10, downAgain.CursorIndex);
    }
}
```

- [ ] **Step 2: Run tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ComposerVisualLayoutTests" --no-restore
```

Expected: FAIL to compile because `ComposerVisualLayout` and `ComposerVisualPosition` do not exist.

- [ ] **Step 3: Implement the exact mapping seam**

Create `ComposerVisualLayout.cs`:

```csharp
using System.Collections.Immutable;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Input;

internal readonly record struct ComposerVisualPosition(int Row, int Column);

internal sealed class ComposerVisualLayout
{
    private readonly string text;
    private readonly ImmutableArray<WrappedCellRow> rows;

    private ComposerVisualLayout(string text, ImmutableArray<WrappedCellRow> rows)
    {
        this.text = text;
        this.rows = rows;
    }

    public int VisualLineCount => this.rows.Length;

    public static ComposerVisualLayout Create(string text, int width)
    {
        text ??= string.Empty;
        return new ComposerVisualLayout(text, TerminalCellText.Wrap(text, Math.Max(1, width)));
    }

    public ComposerVisualPosition PositionForIndex(int utf16Index)
    {
        var index = Math.Clamp(utf16Index, 0, this.text.Length);
        for (var row = 0; row < this.rows.Length; row++)
        {
            var current = this.rows[row];
            if (index < current.StartIndex || index > current.EndIndex)
            {
                continue;
            }

            var column = 0;
            foreach (var element in current.Elements)
            {
                if (element.Utf16Start >= index)
                {
                    break;
                }

                column += element.CellWidth;
            }

            return new ComposerVisualPosition(row, column);
        }

        var last = this.rows[^1];
        return new ComposerVisualPosition(this.rows.Length - 1, last.CellWidth);
    }

    public int IndexForPosition(int row, int displayColumn)
    {
        var current = this.rows[Math.Clamp(row, 0, this.rows.Length - 1)];
        var column = Math.Max(0, displayColumn);
        foreach (var element in current.Elements)
        {
            var end = element.CellStart + Math.Max(1, element.CellWidth);
            if (column < end)
            {
                return element.Utf16Start;
            }
        }

        return current.EndIndex;
    }

    public (int CursorIndex, int PreferredColumn) MoveVertical(
        int utf16Index,
        int delta,
        int? preferredColumn)
    {
        var current = this.PositionForIndex(utf16Index);
        var preferred = preferredColumn ?? current.Column;
        var targetRow = Math.Clamp(current.Row + Math.Sign(delta), 0, this.rows.Length - 1);
        return (this.IndexForPosition(targetRow, preferred), preferred);
    }
}
```

Before accepting green, compare the map to a real laid-out `TextView` in the next task. If a test reveals a Terminal.Gui word-break difference, adjust only `TerminalCellText.Wrap`; do not add reflection or call private TextView wrap internals.

- [ ] **Step 4: Run tests and verify green**

Run the Step 2 command again.

Expected: PASS for explicit newlines, visual wraps, wide/combining graphemes, UTF-16 round trips, and preferred-column movement.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Input\ComposerVisualLayout.cs tests\Coda.Tui.Tests\ComposerVisualLayoutTests.cs
git commit -m "feat(tui): model visual composer layout" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 7: Grow and internally scroll the composer

**Files:**
- Modify: `src/Coda.Tui/Ui/Input/ComposerState.cs`
- Modify: `src/Coda.Tui/Ui/Input/ComposerController.cs`
- Modify: `src/Coda.Tui/Ui/Input/ComposerView.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Modify: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs`
- Modify: `tests/Coda.Tui.Tests/ComposerControllerTests.cs`
- Modify: `tests/Coda.Tui.Tests/ComposerViewTests.cs`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/InlineTuiShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/CommandCompletionShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/TuiControllerTests.cs`

- [ ] **Step 1: Write failing state, laid-out caret, height, cap, and restore tests**

Keep the new fields optional so existing five-argument fixtures mean `ScrollRow = 0` and `PreferredDisplayColumn = null`. Add:

```csharp
[Fact]
public void Export_and_restore_preserve_scroll_and_preferred_column()
{
    var controller = CreateController();
    controller.ReplaceDraft("one\ntwo\nthree", 7);
    controller.UpdateViewport(scrollRow: 2);
    controller.MoveCursorTo(6, preferredDisplayColumn: 4);

    var restored = CreateController();
    restored.Restore(controller.Export());

    Assert.Equal(2, restored.State.ScrollRow);
    Assert.Equal(4, restored.State.PreferredDisplayColumn);
}
```

Add to `ComposerViewTests.cs`:

```csharp
[Fact]
public void Laid_out_view_maps_wrapped_caret_without_headless_origin_regression()
{
    var controller = CreateController();
    using var view = new ComposerView(controller) { Width = 6, Height = 3 };
    view.BeginInit();
    view.EndInit();
    view.Layout(new System.Drawing.Size(6, 3));

    view.SetDraft("alpha beta", 10);

    Assert.Equal(10, controller.State.CursorIndex);
    Assert.Equal(new System.Drawing.Point(4, 1), view.InsertionPoint);
}

[Theory]
[InlineData(9, 3)]
[InlineData(12, 4)]
[InlineData(24, 8)]
[InlineData(40, 8)]
public void Height_cap_is_max_3_min_8_floor_35_percent(int screenHeight, int expected)
{
    Assert.Equal(expected, ComposerView.MaximumHeight(screenHeight));
}

[Fact]
public void Text_beyond_cap_is_preserved_and_scroll_keeps_caret_visible()
{
    var controller = CreateController();
    using var view = CreateLaidOutView(controller, width: 8, height: 3);
    view.SetDraft("one two three four five six seven eight", 39);

    view.ApplyViewport(width: 8, height: 3);

    Assert.Equal("one two three four five six seven eight", view.GetDraft());
    Assert.True(controller.State.ScrollRow > 0);
    Assert.InRange(
        view.InsertionPoint.Y,
        controller.State.ScrollRow,
        controller.State.ScrollRow + 2);
}
```

Change `CreateLaidOutView` to accept width/height parameters and call the public init/layout sequence.

Add this shell theory:

```csharp
[Theory]
[InlineData(80, 24, 8)]
[InlineData(80, 12, 4)]
public void Composer_grows_to_wrapped_content_then_caps(int width, int height, int cap)
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(width, height);
    using var shell = ShellTestFactory.CreateFullscreen(app);
    var token = app.Begin(shell);
    app.LayoutAndDraw();

    Assert.Equal(3, shell.Composer.Frame.Height);
    shell.Composer.SetDraft(string.Join(' ', Enumerable.Repeat("wrapped", 40)), 319);
    app.LayoutAndDraw();

    Assert.Equal(cap, shell.Composer.Frame.Height);
    Assert.Equal(shell.Operational.Frame.Y, shell.Completion.Frame.Bottom);
    Assert.Equal(shell.Status.Frame.Y, shell.Composer.Frame.Bottom);
    Assert.Equal(shell.Composer.Frame.Y, shell.Operational.Frame.Bottom);

    if (token is not null)
    {
        app.End(token);
    }
}

[Fact]
public void Failed_layout_measurement_keeps_previous_valid_height()
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(80, 24);
    using var shell = ShellTestFactory.CreateFullscreen(app);
    var token = app.Begin(shell);
    app.LayoutAndDraw();
    var previous = shell.Composer.Frame.Height;
    shell.Composer.LayoutFactory = (_, _) =>
        throw new InvalidOperationException("measurement failed");

    shell.Composer.SetDraft("trigger layout", 14);
    app.LayoutAndDraw();

    Assert.Equal(previous, shell.Composer.Frame.Height);

    if (token is not null)
    {
        app.End(token);
    }
}
```

Add to `CommandCompletionShellTests.cs`:

```csharp
[Fact]
public void Paste_completion_history_and_resize_each_recalculate_composer_layout()
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(24, 24);
    var controller = new ComposerController(
        new SlashCommandCompletion(new SlashCommandRegistry(Commands())));
    controller.SeedHistory([string.Join(' ', Enumerable.Repeat("history", 20))]);
    using var shell = new FullscreenTuiShell(
        app,
        controller,
        new RecordingUiEvents(),
        UiSessionSnapshot.Empty);
    var token = app.Begin(shell);
    app.LayoutAndDraw();

    var beforePaste = shell.ComposerLayoutUpdateCount;
    shell.Composer.NewPasteEvent(string.Join(' ', Enumerable.Repeat("paste", 20)));
    app.LayoutAndDraw();
    Assert.True(shell.ComposerLayoutUpdateCount > beforePaste);
    Assert.True(shell.Composer.Frame.Height > 3);

    var beforeTyping = shell.ComposerLayoutUpdateCount;
    shell.Composer.NewKeyDownEvent(new Key('x'));
    app.LayoutAndDraw();
    Assert.True(shell.ComposerLayoutUpdateCount > beforeTyping);

    shell.Composer.SetDraft("a\nb\nc\nd", 7);
    app.LayoutAndDraw();
    Assert.Equal(4, shell.Composer.Frame.Height);
    var beforeDeletion = shell.ComposerLayoutUpdateCount;
    shell.Composer.NewKeyDownEvent(Key.Backspace);
    shell.Composer.NewKeyDownEvent(Key.Backspace);
    app.LayoutAndDraw();
    Assert.True(shell.ComposerLayoutUpdateCount > beforeDeletion);
    Assert.Equal(3, shell.Composer.Frame.Height);

    shell.Composer.SetDraft("/he", 3);
    var beforeCompletion = shell.ComposerLayoutUpdateCount;
    shell.Composer.NewKeyDownEvent(Key.Tab);
    app.LayoutAndDraw();
    Assert.True(shell.ComposerLayoutUpdateCount > beforeCompletion);

    shell.Composer.SetDraft(string.Empty, 0);
    var beforeHistory = shell.ComposerLayoutUpdateCount;
    shell.Composer.NewKeyDownEvent(Key.CursorUp);
    app.LayoutAndDraw();
    Assert.True(shell.ComposerLayoutUpdateCount > beforeHistory);
    Assert.Contains("history", shell.Composer.GetDraft(), StringComparison.Ordinal);

    var beforeResize = shell.ComposerLayoutUpdateCount;
    app.Driver.SetScreenSize(50, 12);
    app.LayoutAndDraw();
    Assert.True(shell.ComposerLayoutUpdateCount > beforeResize);
    Assert.InRange(shell.Composer.Frame.Height, 3, 4);

    if (token is not null)
    {
        app.End(token);
    }
}
```

Add to `InlineTuiShellTests.cs`:

```csharp
[Fact]
public void Mode_restore_preserves_draft_caret_scroll_height_and_focus()
{
    var state = new ComposerState(
        Draft: string.Join(' ', Enumerable.Repeat("restored", 20)),
        CursorIndex: 80,
        History: ["older"],
        HistoryIndex: 1,
        PasteActive: false,
        ScrollRow: 2,
        PreferredDisplayColumn: 5);

    using IApplication app = Application.Create();
    app.AppModel = AppModel.Inline;
    app.ForceInlinePosition = new Point(0, 0);
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(24, 18);
    app.Driver.InlinePosition = new Point(0, 0);
    using var shell = ShellTestFactory.CreateInline(app);
    shell.RestoreComposerState(state);
    var token = app.Begin(shell);
    app.LayoutAndDraw();

    var restored = shell.ExportComposerState();
    Assert.Equal(state.Draft, restored.Draft);
    Assert.Equal(state.CursorIndex, restored.CursorIndex);
    Assert.Equal(state.ScrollRow, restored.ScrollRow);
    Assert.Equal(state.PreferredDisplayColumn, restored.PreferredDisplayColumn);
    Assert.True(shell.Composer.Frame.Height > 3);
    Assert.True(shell.Composer.HasFocus);

    if (token is not null)
    {
        app.End(token);
    }
}
```

- [ ] **Step 2: Run composer/shell tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ComposerControllerTests|FullyQualifiedName~ComposerViewTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests|FullyQualifiedName~CommandCompletionShellTests|FullyQualifiedName~TuiControllerTests" --no-restore
```

Expected: FAIL because `ComposerState` lacks viewport fields, wrapped caret still uses logical rows, and shell/composer height remains fixed at three.

- [ ] **Step 3: Extend transferable composer state and controller seams**

Use this exact state:

```csharp
public sealed record ComposerState(
    string Draft,
    int CursorIndex,
    ImmutableArray<string> History,
    int HistoryIndex,
    bool PasteActive,
    int ScrollRow = 0,
    int? PreferredDisplayColumn = null)
{
    public static ComposerState Empty { get; } =
        new(string.Empty, 0, [], 0, false, 0, null);
}
```

Add to `ComposerController`:

```csharp
public void UpdateViewport(int scrollRow) =>
    this.State = this.State with { ScrollRow = Math.Max(0, scrollRow) };

public void MoveCursorTo(int cursorIndex, int? preferredDisplayColumn = null)
{
    var clamped = Math.Clamp(cursorIndex, 0, this.State.Draft.Length);
    this.State = this.State with
    {
        CursorIndex = clamped,
        PreferredDisplayColumn = preferredDisplayColumn,
    };
    this.RefreshCompletion();
}

public void ResetPreferredDisplayColumn() =>
    this.State = this.State with { PreferredDisplayColumn = null };
```

All text mutation, horizontal movement, submission, completion, and history replacement paths reset `PreferredDisplayColumn` to null. `Restore` clamps `ScrollRow` to non-negative and preserves a non-negative preferred column. `Submit` returns `ComposerState.Empty` with preserved history.

- [ ] **Step 4: Make `ComposerView` measure, mirror, and scroll visual rows**

Add:

```csharp
public event EventHandler? LayoutInvalidated;
internal Func<string, int, ComposerVisualLayout> LayoutFactory { get; set; } =
    ComposerVisualLayout.Create;

internal static int MaximumHeight(int screenHeight) =>
    Math.Max(3, Math.Min(8, (int)Math.Floor(Math.Max(0, screenHeight) * 0.35)));

internal ComposerVisualLayout MeasureLayout(int width) =>
    this.LayoutFactory(this.controller.State.Draft, Math.Max(1, width));

internal int DesiredHeight(int width, int screenHeight)
{
    var visualRows = this.MeasureLayout(width).VisualLineCount;
    return Math.Min(MaximumHeight(screenHeight), Math.Max(3, visualRows));
}

internal void ApplyViewport(int width, int height)
{
    var layout = this.MeasureLayout(width);
    var caret = layout.PositionForIndex(this.controller.State.CursorIndex);
    var top = this.controller.State.ScrollRow;
    if (caret.Row < top)
    {
        top = caret.Row;
    }
    else if (caret.Row >= top + Math.Max(1, height))
    {
        top = caret.Row - Math.Max(1, height) + 1;
    }

    top = Math.Clamp(top, 0, Math.Max(0, layout.VisualLineCount - Math.Max(1, height)));
    this.controller.UpdateViewport(top);
    this.ScrollTo(new System.Drawing.Point(0, top));
    this.InsertionPoint = layout.PositionForIndex(this.controller.State.CursorIndex) is var position
        ? new System.Drawing.Point(position.Column, position.Row)
        : default;
}
```

Replace current logical-line `ApplyCursor` and `FlatCursorIndex` with `ComposerVisualLayout.PositionForIndex` and `IndexForPosition`. Keep `SyncCursorFromView` guarded by `IsInitialized`; laid-out tests are the source of truth. Raise `LayoutInvalidated` after set-draft, printable edit, base TextView edit, paste, completion, history, submit, and restore. Do not derive layout from an uninitialized `InsertionPoint`.

- [ ] **Step 5: Make shell geometry depend on measured composer height**

In `FullscreenTuiShell`, add:

```csharp
private int composerHeight = MinimumComposerHeight;
private bool applyingComposerLayout;

internal int ComposerHeight => this.composerHeight;
internal int ComposerLayoutUpdateCount { get; private set; }

protected override void OnSubViewsLaidOut(LayoutEventArgs args)
{
    base.OnSubViewsLaidOut(args);
    this.RecalculateComposerLayout();
}

private void RecalculateComposerLayout()
{
    this.ComposerLayoutUpdateCount++;
    if (this.applyingComposerLayout)
    {
        return;
    }

    var screenHeight = this.HostApp.Screen.Height > 0
        ? this.HostApp.Screen.Height
        : this.Frame.Height;
    var width = Math.Max(1, this.Frame.Width - ComposerGutterWidth);
    int next;
    try
    {
        next = this.Composer.DesiredHeight(width, screenHeight);
    }
    catch (Exception)
    {
        return;
    }
    if (next == this.composerHeight)
    {
        this.Composer.ApplyViewport(width, next);
        return;
    }

    this.applyingComposerLayout = true;
    try
    {
        this.composerHeight = next;
        this.ApplyBottomAnchors();
        this.PlaceCompletion(this.Completion.DesiredHeight, this.Completion.Visible);
        this.Composer.ApplyViewport(width, next);
        this.SetNeedsLayout();
    }
    finally
    {
        this.applyingComposerLayout = false;
    }
}
```

`ApplyBottomAnchors` must set transcript bottom reserve, operational/composer/chrome/metadata anchors exactly as Task 5, using the current `composerHeight`. Subscribe to `Composer.LayoutInvalidated` in `TerminalGuiShellBase`, call an abstract `OnComposerLayoutInvalidated`, implement it as `RecalculateComposerLayout`, and unsubscribe on disposal.

`RestoreComposerState` must call `Composer.SetDraft`, restore viewport state, request layout, and focus only after initialization/prompt checks.

- [ ] **Step 6: Run focused tests and verify green**

Run the Step 2 command again.

Expected: PASS. The composer starts at three, grows on typing/paste/completion/history, caps at `max(3,min(8,floor(screen*0.35)))`, remeasures on resize/mode restore, scrolls internally, and keeps its laid-out caret visible without losing text.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Tui\Ui\Input\ComposerState.cs src\Coda.Tui\Ui\Input\ComposerController.cs src\Coda.Tui\Ui\Input\ComposerView.cs src\Coda.Tui\Ui\Shells\TerminalGuiShellBase.cs src\Coda.Tui\Ui\Shells\FullscreenTuiShell.cs tests\Coda.Tui.Tests\ComposerControllerTests.cs tests\Coda.Tui.Tests\ComposerViewTests.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs tests\Coda.Tui.Tests\InlineTuiShellTests.cs tests\Coda.Tui.Tests\CommandCompletionShellTests.cs tests\Coda.Tui.Tests\TuiControllerTests.cs
git commit -m "feat(tui): grow and scroll the composer" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 8: Implement multiline visual navigation and explicit history chords

**Files:**
- Modify: `src/Coda.Tui/Ui/Input/UiAction.cs`
- Modify: `src/Coda.Tui/Ui/Input/UiActionMap.cs`
- Modify: `src/Coda.Tui/Ui/Input/ComposerController.cs`
- Modify: `src/Coda.Tui/Ui/Input/ComposerView.cs`
- Modify: `tests/Coda.Tui.Tests/ComposerControllerTests.cs`
- Modify: `tests/Coda.Tui.Tests/ComposerViewTests.cs`

- [ ] **Step 1: Replace Up/Down history assumptions with the approved precedence tests**

Update `UiActionMapTests` contexts:

```csharp
private static readonly UiInputContext Empty =
    new(true, false, false, false);
private static readonly UiInputContext TypingMiddle =
    new(false, false, true, true);
private static readonly UiInputContext TypingTop =
    new(false, false, false, true);
private static readonly UiInputContext TypingBottom =
    new(false, false, true, false);
private static readonly UiInputContext Completing =
    new(false, true, true, true);
```

Add:

```csharp
[Fact]
public void Up_down_precedence_is_completion_then_visual_editor_then_history_boundary()
{
    Assert.Equal(UiAction.CompletionPrevious, UiActionMap.Map(Key.CursorUp, Completing));
    Assert.Equal(UiAction.CompletionNext, UiActionMap.Map(Key.CursorDown, Completing));
    Assert.Equal(UiAction.CursorVisualUp, UiActionMap.Map(Key.CursorUp, TypingMiddle));
    Assert.Equal(UiAction.CursorVisualDown, UiActionMap.Map(Key.CursorDown, TypingMiddle));
    Assert.Equal(UiAction.HistoryPrevious, UiActionMap.Map(Key.CursorUp, TypingTop));
    Assert.Equal(UiAction.HistoryNext, UiActionMap.Map(Key.CursorDown, TypingBottom));
}

[Fact]
public void Ctrl_up_down_always_navigate_history_and_ctrl_d_is_unmapped()
{
    Assert.Equal(UiAction.HistoryPrevious, UiActionMap.Map(Key.CursorUp.WithCtrl, TypingMiddle));
    Assert.Equal(UiAction.HistoryNext, UiActionMap.Map(Key.CursorDown.WithCtrl, TypingMiddle));
    Assert.Equal(UiAction.None, UiActionMap.Map(Key.D.WithCtrl, Empty));
}
```

Add laid-out view tests:

```csharp
[Fact]
public void Up_down_move_by_visual_wrapped_line_and_preserve_column()
{
    var controller = CreateController();
    using var view = CreateLaidOutView(controller, width: 5, height: 3);
    view.SetDraft("1234512\n123456", 4);

    view.NewKeyDownEvent(Key.CursorDown);
    Assert.Equal(7, controller.State.CursorIndex);
    Assert.Equal(4, controller.State.PreferredDisplayColumn);

    view.NewKeyDownEvent(Key.CursorDown);
    Assert.Equal(12, controller.State.CursorIndex);
    Assert.Equal(4, controller.State.PreferredDisplayColumn);

    view.NewKeyDownEvent(Key.CursorUp);
    Assert.Equal(7, controller.State.CursorIndex);
}

[Fact]
public void Boundary_up_uses_history_but_ctrl_up_uses_history_from_any_visual_row()
{
    var controller = CreateController();
    controller.SeedHistory(["older"]);
    using var view = CreateLaidOutView(controller, width: 5, height: 3);
    view.SetDraft("abcdefghij", 7);

    view.NewKeyDownEvent(Key.CursorUp.WithCtrl);
    Assert.Equal("older", view.GetDraft());

    view.SetDraft("abcdefghij", 2);
    view.NewKeyDownEvent(Key.CursorUp);
    Assert.Equal("older", view.GetDraft());
}

[Fact]
public void Horizontal_edit_and_history_actions_reset_preferred_display_column()
{
    var controller = CreateController();
    controller.SeedHistory(["older"]);
    controller.ReplaceDraft("abcdef", 4);
    controller.MoveCursorTo(4, preferredDisplayColumn: 4);

    controller.Apply(UiAction.CursorLeft);
    Assert.Null(controller.State.PreferredDisplayColumn);

    controller.MoveCursorTo(3, preferredDisplayColumn: 3);
    controller.InsertText("x");
    Assert.Null(controller.State.PreferredDisplayColumn);

    controller.MoveCursorTo(4, preferredDisplayColumn: 4);
    controller.Apply(UiAction.HistoryPrevious);
    Assert.Null(controller.State.PreferredDisplayColumn);
}
```

- [ ] **Step 2: Run input tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~UiActionMapTests|FullyQualifiedName~ComposerControllerTests|FullyQualifiedName~ComposerViewTests" --no-restore
```

Expected: FAIL because `UiInputContext` lacks visual-boundary flags, `CursorVisualUp/Down` do not exist, plain Up/Down always choose history, and Ctrl+D still maps to exit.

- [ ] **Step 3: Add the final action map**

Add `CursorVisualUp` and `CursorVisualDown` to `UiAction`; keep `HistoryPrevious/Next`. Replace `UiInputContext` with:

```csharp
public readonly record struct UiInputContext(
    bool ComposerEmpty,
    bool CompletionVisible,
    bool CanMoveVisualUp,
    bool CanMoveVisualDown);
```

Use this order in `UiActionMap.Map`:

```csharp
if (key == Key.CursorUp.WithCtrl)
{
    return UiAction.HistoryPrevious;
}

if (key == Key.CursorDown.WithCtrl)
{
    return UiAction.HistoryNext;
}

if (key == Key.CursorUp)
{
    return context.CompletionVisible
        ? UiAction.CompletionPrevious
        : context.CanMoveVisualUp
            ? UiAction.CursorVisualUp
            : UiAction.HistoryPrevious;
}

if (key == Key.CursorDown)
{
    return context.CompletionVisible
        ? UiAction.CompletionNext
        : context.CanMoveVisualDown
            ? UiAction.CursorVisualDown
            : UiAction.HistoryNext;
}
```

Delete Ctrl+C and Ctrl+D mapping here. Escape maps to `DismissCompletion` only when `CompletionVisible`; otherwise it returns `None` for shell handling.

- [ ] **Step 4: Route visual movement through the laid-out map**

In `ComposerView.HandleKeyDown`, build context from current layout:

```csharp
var layout = this.MeasureLayout(Math.Max(1, this.Viewport.Width));
var caret = layout.PositionForIndex(this.controller.State.CursorIndex);
var context = new UiInputContext(
    ComposerEmpty: this.controller.State.Draft.Length == 0,
    CompletionVisible: this.controller.Suggestions.Count > 0,
    CanMoveVisualUp: caret.Row > 0,
    CanMoveVisualDown: caret.Row < layout.VisualLineCount - 1);
```

Handle visual actions:

```csharp
case UiAction.CursorVisualUp:
case UiAction.CursorVisualDown:
    var delta = action == UiAction.CursorVisualUp ? -1 : 1;
    var moved = layout.MoveVertical(
        this.controller.State.CursorIndex,
        delta,
        this.controller.State.PreferredDisplayColumn);
    this.controller.MoveCursorTo(moved.CursorIndex, moved.PreferredColumn);
    this.SyncTextView();
    this.ApplyViewport(Math.Max(1, this.Viewport.Width), Math.Max(1, this.Viewport.Height));
    return true;
```

Completion Up/Down remains controller-owned; boundary history remains controller-owned. Horizontal/edit/paste/completion/history actions reset preferred column as specified in Task 7.

- [ ] **Step 5: Run input tests and verify green**

Run the Step 2 command again.

Expected: PASS. Completion wins, visual wrapped rows are next, boundaries transition to history, Ctrl+Up/Down always choose history, preferred display column survives short rows, and Ctrl+D is unmapped.

- [ ] **Step 6: Commit**

```powershell
git add src\Coda.Tui\Ui\Input\UiAction.cs src\Coda.Tui\Ui\Input\UiActionMap.cs src\Coda.Tui\Ui\Input\ComposerController.cs src\Coda.Tui\Ui\Input\ComposerView.cs tests\Coda.Tui.Tests\ComposerControllerTests.cs tests\Coda.Tui.Tests\ComposerViewTests.cs
git commit -m "feat(tui): navigate visual composer lines" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 9: Make the composer the default typing target

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs`
- Modify: `src/Coda.Tui/Ui/Input/ComposerView.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/InlineTuiShellTests.cs`

- [ ] **Step 1: Write failing focus and type-anywhere tests**

Add to `FullscreenTuiShellTests.cs`:

```csharp
[Fact]
public void Initially_ready_shell_focuses_composer_after_initialization()
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(80, 24);
    using var shell = ShellTestFactory.CreateFullscreen(app);

    var token = app.Begin(shell);
    app.LayoutAndDraw();

    Assert.True(shell.Composer.HasFocus);

    if (token is not null)
    {
        app.End(token);
    }
}

[Fact]
public void Printable_key_from_transcript_focuses_and_inserts_into_composer()
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(80, 24);
    using var shell = ShellTestFactory.CreateFullscreen(app);
    var token = app.Begin(shell);
    app.LayoutAndDraw();
    shell.Transcript.SetFocus();

    Assert.True(shell.Transcript.NewKeyDownEvent(new Key('/')));

    Assert.True(shell.Composer.HasFocus);
    Assert.Equal("/", shell.Composer.GetDraft());

    if (token is not null)
    {
        app.End(token);
    }
}

[Fact]
public void Transcript_navigation_stays_in_transcript()
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(80, 24);
    using var shell = ShellTestFactory.CreateFullscreen(app);
    var token = app.Begin(shell);
    app.LayoutAndDraw();
    shell.Transcript.ReplaceAll(Lines(100));
    shell.Transcript.SetFocus();
    var before = shell.Transcript.TopRow;

    shell.Transcript.NewKeyDownEvent(Key.PageUp);

    Assert.True(shell.Transcript.HasFocus);
    Assert.True(shell.Transcript.TopRow < before);
    Assert.Equal(string.Empty, shell.Composer.GetDraft());

    if (token is not null)
    {
        app.End(token);
    }
}

[Fact]
public async Task Modal_prompt_never_redirects_printable_input()
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(80, 24);
    using var shell = ShellTestFactory.CreateFullscreen(app);
    var token = app.Begin(shell);
    app.LayoutAndDraw();
    var prompt = UiPromptRequest.Text("Name");
    await shell.ApplyAsync(
        UiSessionSnapshot.Empty with { PendingPrompt = prompt },
        CancellationToken.None);

    shell.PromptOverlay.NewKeyDownEvent(new Key('x'));

    Assert.Equal("x", shell.PromptOverlay.BodyText);
    Assert.Equal(string.Empty, shell.Composer.GetDraft());
    Assert.True(shell.PromptOverlay.HasFocus);

    if (token is not null)
    {
        app.End(token);
    }
}
```

Add an inline mode-switch/restore test that constructs a ready inline shell from a non-empty `ComposerState`, begins/layouts it, and asserts draft, caret, scroll row, height, and composer focus are restored.

- [ ] **Step 2: Run retained-shell tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests" --no-restore
```

Expected: FAIL because an initially ready shell does not focus after `Begin`, transcript printable input is unhandled, and mode restoration does not guarantee focus.

- [ ] **Step 3: Add one shell-level unhandled-key seam**

In `VirtualizedTranscriptView`:

```csharp
internal event Func<Key, bool>? UnhandledKeyDown;

protected override bool OnKeyDown(Key key)
{
    if (key is null)
    {
        return false;
    }

    if (key == Key.PageUp)
    {
        this.ScrollBy(-this.PageStep());
        return true;
    }

    if (key == Key.PageDown)
    {
        this.ScrollBy(this.PageStep());
        return true;
    }

    if (key == Key.CursorUp)
    {
        this.ScrollBy(-1);
        return true;
    }

    if (key == Key.CursorDown)
    {
        this.ScrollBy(1);
        return true;
    }

    if (key == Key.Home.WithCtrl)
    {
        this.viewport.ScrollToTop();
        this.SetNeedsDraw();
        this.TranscriptScrolled?.Invoke();
        return true;
    }

    if (key == Key.End.WithCtrl)
    {
        this.JumpToNewest();
        return true;
    }

    if (key == Key.Enter || key == Key.Space)
    {
        if (this.selectedBlockId is { } id)
        {
            this.ToggleExpansion(id);
        }

        return true;
    }

    return this.UnhandledKeyDown?.Invoke(key) == true || base.OnKeyDown(key);
}
```

In `ComposerView`:

```csharp
internal Func<Key, bool>? ShellKeyHandler { get; set; }

internal void InsertFromShell(string text)
{
    if (!this.InputEnabled || string.IsNullOrEmpty(text))
    {
        return;
    }

    this.controller.InsertText(text);
    this.SyncTextView();
    this.RaiseCompletionIfChanged();
    this.LayoutInvalidated?.Invoke(this, EventArgs.Empty);
}
```

At the start of `ComposerView.OnKeyDown`, after the startup-disabled guard:

```csharp
if (this.ShellKeyHandler?.Invoke(key) == true)
{
    return true;
}
```

- [ ] **Step 4: Route only printable transcript input and restore focus safely**

In `TerminalGuiShellBase`, subscribe/unsubscribe `Transcript.UnhandledKeyDown` from the concrete shell through protected methods:

```csharp
protected void BindTranscriptInput(VirtualizedTranscriptView transcript)
{
    transcript.UnhandledKeyDown += this.HandleUnhandledShellKey;
}

protected void UnbindTranscriptInput(VirtualizedTranscriptView transcript)
{
    transcript.UnhandledKeyDown -= this.HandleUnhandledShellKey;
}
```

Use:

```csharp
private bool HandleUnhandledShellKey(Key key)
{
    if (this.PromptOverlay.Visible || !TryGetPrintable(key, out var text))
    {
        return false;
    }

    this.Composer.SetFocus();
    this.Composer.InsertFromShell(text);
    return true;
}

private static bool TryGetPrintable(Key key, out string text)
{
    text = string.Empty;
    if (key is null || key.IsCtrl || key.IsAlt)
    {
        return false;
    }

    var rune = key.AsRune;
    if (rune.Value == 0 || Rune.IsControl(rune))
    {
        return false;
    }

    text = rune.ToString();
    return true;
}
```

Subscribe to the shell's public `Initialized` event in the constructor:

```csharp
this.Initialized += this.OnShellInitialized;
```

and implement:

```csharp
private void OnShellInitialized(object? sender, EventArgs args)
{
    if (!this.composerDisabled &&
        this.Snapshot.PendingPrompt is null &&
        !this.PromptOverlay.Visible)
    {
        this.Composer.SetFocus();
    }
}
```

Use this prompt focus logic:

```csharp
private void UpdatePrompt(UiSessionSnapshot snapshot)
{
    if (snapshot.PendingPrompt is { } prompt)
    {
        this.PromptOverlay.Update(prompt);
        this.PromptOverlay.SetFocus();
        return;
    }

    if (!this.PromptOverlay.Visible)
    {
        return;
    }

    this.PromptOverlay.Update(null);
    if (!this.composerDisabled)
    {
        this.Composer.SetFocus();
    }
}
```

Delete `focusBeforePrompt`; composer-first behavior no longer restores transcript focus. Unsubscribe `Initialized` on disposal. The ready-after-startup branch remains:

```csharp
this.composerDisabled = false;
this.SyncCompletion();
if (snapshot.PendingPrompt is null && !this.PromptOverlay.Visible)
{
    this.Composer.SetFocus();
}
```

- [ ] **Step 5: Run retained-shell tests and verify green**

Run the Step 2 command again.

Expected: PASS. Ready startup, prompt close, and mode restore focus the composer; printable transcript keys insert; transcript navigation remains local; modal prompt input never reaches the composer.

- [ ] **Step 6: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\VirtualizedTranscriptView.cs src\Coda.Tui\Ui\Input\ComposerView.cs src\Coda.Tui\Ui\Shells\TerminalGuiShellBase.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs tests\Coda.Tui.Tests\InlineTuiShellTests.cs
git commit -m "feat(tui): focus composer and type anywhere" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 10: Add deterministic Esc interrupt and Ctrl+C exit chords

**Files:**
- Create: `src/Coda.Tui/Ui/Shells/ShellCommandChordState.cs`
- Create: `tests/Coda.Tui.Tests/ShellCommandChordStateTests.cs`
- Create: `tests/Coda.Tui.Tests/ManualTimeProvider.cs`
- Create: `tests/Coda.Tui.Tests/RetainedShellFixture.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Modify: `src/Coda.Tui/Ui/Input/UiActionMap.cs`
- Modify: `src/Coda.Tui/Ui/TuiController.cs`
- Modify: `src/Coda.Tui/InteractiveProgram.cs:322-365`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/TuiControllerTests.cs`

- [ ] **Step 1: Add exact reusable clock/shell test seams and write failing monotonic-state tests**

Create `ManualTimeProvider.cs`:

```csharp
namespace Coda.Tui.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private long timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => this.timestamp;

    public void Advance(TimeSpan duration) => this.timestamp += duration.Ticks;
}
```

Create `RetainedShellFixture.cs`:

```csharp
using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

internal sealed class RetainedShellFixture : IDisposable
{
    private readonly IApplication app;
    private readonly SessionToken? token;
    private bool disposed;

    private RetainedShellFixture(
        IApplication app,
        FullscreenTuiShell shell,
        SessionToken? token)
    {
        this.app = app;
        this.Shell = shell;
        this.token = token;
        this.Shell.ActionRequested += (_, action) => this.Actions.Add(action);
    }

    internal FullscreenTuiShell Shell { get; }
    internal List<UiAction> Actions { get; } = [];

    internal static RetainedShellFixture Create(
        bool activeWork,
        IEnumerable<ISlashCommand>? commands = null,
        TimeProvider? timeProvider = null,
        Func<string, bool>? clipboardWriter = null,
        Func<TimeSpan, Func<bool>, object>? addTimeout = null,
        Func<object, bool>? removeTimeout = null)
    {
        IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        var controller = new ComposerController(
            new SlashCommandCompletion(
                new SlashCommandRegistry(commands ?? [])));
        var shell = new FullscreenTuiShell(
            app,
            controller,
            new RecordingUiEvents(),
            UiSessionSnapshot.Empty,
            hasActiveWork: () => activeWork,
            timeProvider: timeProvider,
            clipboardWriter: clipboardWriter,
            addTimeout: addTimeout,
            removeTimeout: removeTimeout);
        var token = app.Begin(shell);
        app.LayoutAndDraw();
        return new RetainedShellFixture(app, shell, token);
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        if (this.token is not null)
        {
            this.app.End(this.token);
        }

        this.Shell.Dispose();
        this.app.Dispose();
    }
}
```

```csharp
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class ShellCommandChordStateTests
{
    [Fact]
    public void Escape_arms_then_interrupts_within_800ms()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);

        var first = state.HandleEscape(hasActiveWork: true);
        Assert.Equal(
            new OperationalStatus(
                "Press Esc again to interrupt",
                OperationalTone.Warning,
                Animated: false),
            first.Hint);
        Assert.Equal(ShellChordAction.None, first.Action);

        clock.Advance(TimeSpan.FromMilliseconds(799));
        var second = state.HandleEscape(hasActiveWork: true);
        Assert.Equal(ShellChordAction.Interrupt, second.Action);
        Assert.Null(second.Hint);
    }

    [Fact]
    public void Expired_escape_window_rearms_instead_of_interrupting()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);
        state.HandleEscape(hasActiveWork: true);

        clock.Advance(TimeSpan.FromMilliseconds(801));
        var result = state.HandleEscape(hasActiveWork: true);

        Assert.Equal(ShellChordAction.None, result.Action);
        Assert.Equal("Press Esc again to interrupt", result.Hint!.Text);
    }

    [Fact]
    public void Ctrl_c_arms_then_exits_within_1500ms()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);

        var first = state.HandleCtrlC();
        clock.Advance(TimeSpan.FromMilliseconds(1499));
        var second = state.HandleCtrlC();

        Assert.Equal("Press Ctrl+C again to exit", first.Hint!.Text);
        Assert.Equal(ShellChordAction.Exit, second.Action);
    }

    [Fact]
    public void Reset_clears_hint_and_armed_action()
    {
        var state = new ShellCommandChordState(new ManualTimeProvider());
        state.HandleCtrlC();

        state.Reset();

        Assert.Null(state.CurrentHint);
        Assert.Equal(ShellChordAction.None, state.ArmedAction);
    }

}
```

Add `Warning` to `OperationalTone` and map it to Warm Ember warning yellow.

- [ ] **Step 2: Write failing shell arbitration tests**

Add to `FullscreenTuiShellTests.cs`:

```csharp
[Fact]
public void First_escape_dismisses_completion_before_arming_interrupt()
{
    using var fixture = RetainedShellFixture.Create(
        activeWork: true,
        commands: SlashCommandCatalog.CreateAll());
    fixture.Shell.Composer.SetDraft("/m", 2);
    Assert.True(fixture.Shell.Completion.Visible);

    fixture.Shell.Composer.NewKeyDownEvent(Key.Esc);

    Assert.False(fixture.Shell.Completion.Visible);
    Assert.DoesNotContain("Press Esc again", fixture.Shell.Operational.Status.Text);
    Assert.Empty(fixture.Actions);
}

[Fact]
public void Escape_arms_then_interrupts_and_ctrl_c_arms_then_exits()
{
    var clock = new ManualTimeProvider();
    using var fixture = RetainedShellFixture.Create(activeWork: true, timeProvider: clock);

    fixture.Shell.Composer.NewKeyDownEvent(Key.Esc);
    Assert.Equal("Press Esc again to interrupt", fixture.Shell.Operational.Status.Text);
    clock.Advance(TimeSpan.FromMilliseconds(200));
    fixture.Shell.Composer.NewKeyDownEvent(Key.Esc);
    Assert.Equal([UiAction.Interrupt], fixture.Actions);

    fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);
    Assert.Equal("Press Ctrl+C again to exit", fixture.Shell.Operational.Status.Text);
    clock.Advance(TimeSpan.FromSeconds(1));
    fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);
    Assert.Equal([UiAction.Interrupt, UiAction.Exit], fixture.Actions);
}

[Fact]
public async Task Prompt_activation_and_mode_switch_reset_armed_chords()
{
    var clock = new ManualTimeProvider();
    using var fixture = RetainedShellFixture.Create(activeWork: true, timeProvider: clock);
    fixture.Shell.Composer.NewKeyDownEvent(Key.Esc);

    await fixture.Shell.ApplyAsync(
        UiSessionSnapshot.Empty with { PendingPrompt = UiPromptRequest.Confirm("Allow?", false) },
        CancellationToken.None);
    Assert.DoesNotContain("Press Esc again", fixture.Shell.Operational.Status.Text);

    await fixture.Shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
    fixture.Shell.Composer.NewKeyDownEvent(Key.Esc);
    fixture.Shell.Composer.NewKeyDownEvent(Key.F2);
    Assert.DoesNotContain("Press Esc again", fixture.Shell.Operational.Status.Text);
}
```

Replace old `TuiControllerTests` that expect idle Ctrl+C notification with:

```csharp
[Fact]
public async Task Interrupt_action_calls_interrupt_without_requesting_exit()
{
    var interrupts = 0;
    var controller = new TuiController(
        dispatch: (_, _) => Task.CompletedTask,
        tryInterrupt: () =>
        {
            interrupts++;
            return true;
        },
        publisher: new RecordingUiEvents(),
        initialSnapshot: UiSessionSnapshot.Empty);

    await controller.HandleActionAsync(UiAction.Interrupt);

    Assert.Equal(1, interrupts);
    Assert.False(controller.ExitRequested);
}
```

- [ ] **Step 3: Run chord/controller tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ShellCommandChordStateTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~TuiControllerTests|FullyQualifiedName~UiActionMapTests" --no-restore
```

Expected: FAIL because chord state/operational warning tone do not exist, Ctrl+C still immediately emits interrupt from the composer map, and Ctrl+D still has legacy expectations.

- [ ] **Step 4: Implement the monotonic chord state**

Create `ShellCommandChordState.cs`:

```csharp
using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui.Shells;

internal enum ShellChordAction
{
    None,
    Interrupt,
    Exit,
}

internal readonly record struct ShellChordResult(
    bool Consumed,
    ShellChordAction Action,
    OperationalStatus? Hint);

internal sealed class ShellCommandChordState
{
    internal static readonly TimeSpan InterruptWindow = TimeSpan.FromMilliseconds(800);
    internal static readonly TimeSpan ExitWindow = TimeSpan.FromMilliseconds(1500);

    private readonly TimeProvider clock;
    private long armedAt;

    public ShellCommandChordState(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
    }

    internal ShellChordAction ArmedAction { get; private set; }
    internal OperationalStatus? CurrentHint { get; private set; }

    internal ShellChordResult HandleEscape(bool hasActiveWork)
    {
        if (!hasActiveWork)
        {
            this.Reset();
            return new(false, ShellChordAction.None, null);
        }

        return this.Handle(
            ShellChordAction.Interrupt,
            InterruptWindow,
            new OperationalStatus(
                "Press Esc again to interrupt",
                OperationalTone.Warning,
                false));
    }

    internal ShellChordResult HandleCtrlC() =>
        this.Handle(
            ShellChordAction.Exit,
            ExitWindow,
            new OperationalStatus(
                "Press Ctrl+C again to exit",
                OperationalTone.Warning,
                false));

    internal bool Expire()
    {
        if (this.ArmedAction == ShellChordAction.None)
        {
            return false;
        }

        var window = this.ArmedAction == ShellChordAction.Interrupt
            ? InterruptWindow
            : ExitWindow;
        if (this.clock.GetElapsedTime(this.armedAt, this.clock.GetTimestamp()) <= window)
        {
            return false;
        }

        this.Reset();
        return true;
    }

    internal void Reset()
    {
        this.ArmedAction = ShellChordAction.None;
        this.CurrentHint = null;
        this.armedAt = 0;
    }

    private ShellChordResult Handle(
        ShellChordAction action,
        TimeSpan window,
        OperationalStatus hint)
    {
        var now = this.clock.GetTimestamp();
        if (this.ArmedAction == action &&
            this.clock.GetElapsedTime(this.armedAt, now) <= window)
        {
            this.Reset();
            return new(true, action, null);
        }

        this.ArmedAction = action;
        this.armedAt = now;
        this.CurrentHint = hint;
        return new(true, ShellChordAction.None, hint);
    }
}
```

- [ ] **Step 5: Put chord arbitration before composer action mapping**

Add this explicit completion-dismiss seam to `ComposerView`:

```csharp
internal bool DismissCompletion()
{
    if (this.controller.Suggestions.Count == 0)
    {
        return false;
    }

    this.controller.Apply(UiAction.DismissCompletion);
    this.SyncTextView();
    this.RaiseCompletionIfChanged();
    return true;
}
```

In `TerminalGuiShellBase`, set:

```csharp
this.chords = new ShellCommandChordState(timeProvider);
this.hasActiveWork = hasActiveWork ?? (() => false);
this.Composer.ShellKeyHandler = this.TryHandleShellKey;
```

Use this order:

```csharp
private bool TryHandleShellKey(Key key)
{
    if (this.PromptOverlay.Visible)
    {
        return false;
    }

    if (key == Key.Esc)
    {
        if (this.Completion.Visible)
        {
            this.Composer.DismissCompletion();
            return true;
        }

        if (this.TryClearTranscriptSelection())
        {
            return true;
        }

        if (this.TryClearTransientOperationalOverride())
        {
            return true;
        }

        if (this.chords.ArmedAction == ShellChordAction.Exit)
        {
            this.ResetChordOverride();
            return true;
        }

        return this.ApplyChord(
            this.chords.HandleEscape(this.HasInterruptibleWork()));
    }

    if (key == Key.C.WithCtrl)
    {
        if (this.TryCopyTranscriptSelection())
        {
            return true;
        }

        return this.ApplyChord(this.chords.HandleCtrlC());
    }

    return false;
}
```

`HasInterruptibleWork()` returns true when the injected controller delegate is true, an incomplete tool exists, an active operation exists, or `Snapshot.RunningTasks > 0`.

Implement the shell-local override/timer methods exactly:

```csharp
private bool HasInterruptibleWork()
{
    if (this.hasActiveWork() ||
        this.Snapshot.ActiveOperation is not null ||
        this.Snapshot.RunningTasks > 0)
    {
        return true;
    }

    return this.Snapshot.Transcript.Any(
        block => block is ToolTranscriptBlock { Complete: false });
}

private bool ApplyChord(ShellChordResult result)
{
    if (!result.Consumed)
    {
        return false;
    }

    this.StopChordTimeout();
    if (result.Hint is { } hint)
    {
        this.Operational.SetStatus(hint);
        var window = this.chords.ArmedAction == ShellChordAction.Interrupt
            ? ShellCommandChordState.InterruptWindow
            : ShellCommandChordState.ExitWindow;
        this.chordTimeout = this.addTimeout(
            window + TimeSpan.FromMilliseconds(1),
            () =>
            {
                this.chordTimeout = null;
                if (this.disposed)
                {
                    return false;
                }

                this.chords.Expire();
                this.RestoreProjectedOperationalStatus();
                return false;
            });
        return true;
    }

    this.RestoreProjectedOperationalStatus();
    if (result.Action == ShellChordAction.Interrupt)
    {
        this.ActionRequested?.Invoke(this, UiAction.Interrupt);
    }
    else if (result.Action == ShellChordAction.Exit)
    {
        this.ActionRequested?.Invoke(this, UiAction.Exit);
    }

    return true;
}

private void ResetChordOverride()
{
    this.StopChordTimeout();
    this.chords.Reset();
    this.RestoreProjectedOperationalStatus();
}

private void StopChordTimeout()
{
    if (this.chordTimeout is not { } token)
    {
        return;
    }

    this.chordTimeout = null;
    this.removeTimeout(token);
}

private void RestoreProjectedOperationalStatus()
{
    var status = this.transientOperationalOverride ??
        this.chords.CurrentHint ??
        OperationalStatusProjector.Project(this.Snapshot);
    this.Operational.SetStatus(status);
}
```

Store the injected/default delegates and timeout token:

```csharp
private readonly Func<TimeSpan, Func<bool>, object> addTimeout;
private readonly Func<object, bool> removeTimeout;
private object? chordTimeout;
private OperationalStatus? transientOperationalOverride;
```

Initialize them with `addTimeout ?? app.AddTimeout` and `removeTimeout ?? app.RemoveTimeout`. On prompt activation, active-work completion, mode switch action, shell stop, failure, and disposal, call `ResetChordOverride()`.

After assigning `Snapshot` in `Apply`, reset an obsolete interrupt arm:

```csharp
if (this.chords.ArmedAction == ShellChordAction.Interrupt &&
    !this.HasInterruptibleWork())
{
    this.ResetChordOverride();
}
```

Before forwarding `UiAction.ToggleMode` from `OnComposerActionRequested`, call
`ResetChordOverride()`. At the start of `RequestStop`, call
`ResetChordOverride()` and `ClearTransientOperationalOverride()` before
`app.RequestStop()`.

Delete Ctrl+C/Ctrl+D handling from `UiActionMap`. In `TuiController`, delete `IdleNotification` and `HandleCtrlC`; `UiAction.Interrupt` calls `TryInterruptActiveTurn()` once, and `UiAction.Exit` calls `RequestExit()`. In `InteractiveProgram.ShellFactory`, pass:

```csharp
hasActiveWork: () => controller.HasActiveWork
```

to both retained shell constructors. Plain/Spectre paths remain unchanged.

- [ ] **Step 6: Run chord/controller tests and verify green**

Run the Step 3 command again.

Expected: PASS. Transients consume first Esc, active work requires the 800 ms double-Esc, Ctrl+C requires the 1.5 s double chord when no selection exists, `/exit` remains controller-dispatched, and Ctrl+D performs no exit action.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\ShellCommandChordState.cs src\Coda.Tui\Ui\Shells\TerminalGuiShellBase.cs src\Coda.Tui\Ui\Input\UiActionMap.cs src\Coda.Tui\Ui\TuiController.cs src\Coda.Tui\InteractiveProgram.cs tests\Coda.Tui.Tests\ManualTimeProvider.cs tests\Coda.Tui.Tests\RetainedShellFixture.cs tests\Coda.Tui.Tests\ShellCommandChordStateTests.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs tests\Coda.Tui.Tests\TuiControllerTests.cs
git commit -m "feat(tui): add safe interrupt and exit chords" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 11: Add global-row transcript selection and arbitrary-range access

**Files:**
- Create: `src/Coda.Tui/Ui/Shells/TranscriptSelection.cs`
- Create: `tests/Coda.Tui.Tests/TranscriptSelectionTests.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs` (`TranscriptLayoutIndexTests`)

- [ ] **Step 1: Write selection normalization/copy and off-viewport range tests**

```csharp
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

public sealed class TranscriptSelectionTests
{
    [Fact]
    public void Movement_of_one_cell_starts_selection_and_zero_movement_does_not()
    {
        var selection = new TranscriptSelection();
        selection.Begin(new TranscriptCellPosition(10, 3));

        Assert.False(selection.Update(new TranscriptCellPosition(10, 3)));
        Assert.False(selection.HasSelection);
        Assert.True(selection.Update(new TranscriptCellPosition(10, 4)));
        Assert.True(selection.HasSelection);
    }

    [Fact]
    public void Reversed_multirow_selection_normalizes_row_ranges()
    {
        var selection = new TranscriptSelection();
        selection.Begin(new TranscriptCellPosition(4, 2));
        selection.Update(new TranscriptCellPosition(2, 3));

        Assert.Equal((3, 8), selection.RangeForRow(2, rowWidth: 8));
        Assert.Equal((0, 8), selection.RangeForRow(3, rowWidth: 8));
        Assert.Equal((0, 3), selection.RangeForRow(4, rowWidth: 8));
        Assert.Null(selection.RangeForRow(5, rowWidth: 8));
    }

    [Fact]
    public void CopyText_preserves_line_breaks_and_cell_slices()
    {
        var selection = new TranscriptSelection();
        selection.Begin(new TranscriptCellPosition(0, 1));
        selection.Update(new TranscriptCellPosition(2, 1));
        var rows = new[]
        {
            new TranscriptRow(Guid.NewGuid(), 0, 0, "alpha", TranscriptRole.Assistant),
            new TranscriptRow(Guid.NewGuid(), 0, 1, "界beta", TranscriptRole.Tool),
            new TranscriptRow(Guid.NewGuid(), 0, 2, "omega", TranscriptRole.User),
        };

        Assert.Equal("lpha\n界beta\nom", selection.CopyText(rows));
    }
}
```

Add to `TranscriptLayoutIndexTests`:

```csharp
[Fact]
public void GetRows_returns_arbitrary_global_range_beyond_current_viewport()
{
    var blocks = Blocks(1_000);
    var index = new TranscriptLayoutIndex(
        (block, width) => [((CommandOutputTranscriptBlock)block).Text]);
    index.ReplaceAll(blocks, width: 80);

    var rows = index.GetRows(firstRow: 400, count: 250);

    Assert.Equal(250, rows.Count);
    Assert.Equal(400, rows[0].GlobalRow);
    Assert.Equal(649, rows[^1].GlobalRow);
    Assert.Equal("line 400", rows[0].Text);
}
```

- [ ] **Step 2: Run pure selection/index tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptSelectionTests|FullyQualifiedName~TranscriptLayoutIndexTests.GetRows_returns_arbitrary" --no-restore
```

Expected: FAIL to compile because selection types and `TranscriptLayoutIndex.GetRows` do not exist.

- [ ] **Step 3: Implement the selection model**

Create `TranscriptSelection.cs`:

```csharp
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Shells;

internal readonly record struct TranscriptCellPosition(int GlobalRow, int CellColumn);

internal sealed class TranscriptSelection
{
    private bool moved;

    internal TranscriptCellPosition Anchor { get; private set; }
    internal TranscriptCellPosition Active { get; private set; }
    internal bool HasSelection => this.moved;

    internal void Begin(TranscriptCellPosition anchor)
    {
        this.Anchor = Normalize(anchor);
        this.Active = this.Anchor;
        this.moved = false;
    }

    internal bool Update(TranscriptCellPosition active)
    {
        this.Active = Normalize(active);
        this.moved = this.Active != this.Anchor;
        return this.moved;
    }

    internal void Clear()
    {
        this.moved = false;
        this.Anchor = default;
        this.Active = default;
    }

    internal (int StartCell, int EndCellExclusive)? RangeForRow(int globalRow, int rowWidth)
    {
        if (!this.HasSelection)
        {
            return null;
        }

        var (start, end) = this.Ordered();
        if (globalRow < start.GlobalRow || globalRow > end.GlobalRow)
        {
            return null;
        }

        var width = Math.Max(0, rowWidth);
        if (start.GlobalRow == end.GlobalRow)
        {
            return (
                Math.Clamp(start.CellColumn, 0, width),
                Math.Clamp(end.CellColumn + 1, 0, width));
        }

        if (globalRow == start.GlobalRow)
        {
            return (Math.Clamp(start.CellColumn, 0, width), width);
        }

        if (globalRow == end.GlobalRow)
        {
            return (0, Math.Clamp(end.CellColumn + 1, 0, width));
        }

        return (0, width);
    }

    internal string CopyText(IReadOnlyList<TranscriptRow> rows)
    {
        var selected = new List<string>();
        foreach (var row in rows.OrderBy(row => row.GlobalRow))
        {
            var width = TerminalCellText.Width(row.Text);
            if (this.RangeForRow(row.GlobalRow, width) is not { } range)
            {
                continue;
            }

            selected.Add(TerminalCellText.SliceByCells(
                row.Text,
                range.StartCell,
                range.EndCellExclusive));
        }

        return string.Join('\n', selected);
    }

    internal (TranscriptCellPosition Start, TranscriptCellPosition End) Ordered()
    {
        var anchorBeforeActive =
            this.Anchor.GlobalRow < this.Active.GlobalRow ||
            (this.Anchor.GlobalRow == this.Active.GlobalRow &&
             this.Anchor.CellColumn <= this.Active.CellColumn);
        return anchorBeforeActive
            ? (this.Anchor, this.Active)
            : (this.Active, this.Anchor);
    }

    private static TranscriptCellPosition Normalize(TranscriptCellPosition value) =>
        new(Math.Max(0, value.GlobalRow), Math.Max(0, value.CellColumn));
}
```

Selection stores inclusive anchor/active cell positions. `RangeForRow` converts them to half-open slices by adding one only to the ordered end cell. Thus dragging from column 3 to column 4 selects `[3,5)`, while press/release at column 3 keeps `HasSelection == false`.

- [ ] **Step 4: Add arbitrary global-row access without viewport clamping**

In `TranscriptLayoutIndex`, factor row collection into:

```csharp
public IReadOnlyList<TranscriptRow> GetRows(int firstRow, int count)
{
    var start = Math.Clamp(firstRow, 0, this.TotalRows);
    var end = Math.Min(this.TotalRows, start + Math.Max(0, count));
    return this.CollectRows(start, end);
}

public IReadOnlyList<TranscriptRow> GetVisibleRows(int firstRow, int height, int overscan)
{
    var total = this.TotalRows;
    if (total == 0 || height <= 0)
    {
        return Array.Empty<TranscriptRow>();
    }

    var pad = Math.Max(0, overscan);
    var maxTop = Math.Max(0, total - height);
    var effectiveFirst = Math.Clamp(firstRow, 0, maxTop);
    return this.CollectRows(
        Math.Max(0, effectiveFirst - pad),
        Math.Min(total, effectiveFirst + height + pad));
}
```

Use this exact shared collector:

```csharp
private IReadOnlyList<TranscriptRow> CollectRows(int start, int end)
{
    if (start >= end || this.TotalRows == 0)
    {
        return Array.Empty<TranscriptRow>();
    }

    var rows = new List<TranscriptRow>(end - start);
    var blockIndex = this.FindBlock(start);
    while (blockIndex < this.blocks.Length && this.prefix[blockIndex] < end)
    {
        var blockStart = this.prefix[blockIndex];
        var count = this.rowCounts[blockIndex];
        if (count == 0)
        {
            blockIndex++;
            continue;
        }

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

            var line = local < lines.Count ? lines[local] : default;
            rows.Add(new TranscriptRow(
                block.Id,
                local,
                global,
                line.Text ?? string.Empty,
                line.Role));
        }

        blockIndex++;
    }

    return rows;
}
```

It formats/caches only intersecting blocks. `GetRows` may materialize a large selected range because copy needs that text; normal drawing remains viewport-bounded.

- [ ] **Step 5: Run pure tests and verify green**

Run the Step 2 command again.

Expected: PASS. Selection is shell-local/global-row based, copy preserves row breaks, and arbitrary range access is not constrained by the current viewport.

- [ ] **Step 6: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\TranscriptSelection.cs src\Coda.Tui\Ui\Shells\TranscriptLayoutIndex.cs tests\Coda.Tui.Tests\TranscriptSelectionTests.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs
git commit -m "feat(tui): model transcript text selection" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 12: Add drag highlighting and Ctrl+C clipboard arbitration

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Modify: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs` (`VirtualizedTranscriptViewTests` and shell tests)

- [ ] **Step 1: Write failing mouse-selection behavior tests**

Add to `VirtualizedTranscriptViewTests`:

```csharp
[Fact]
public void Zero_movement_click_keeps_expand_behavior_but_drag_selects()
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(80, 24);
    var view = CreateView(app, out _);
    var toolId = Guid.NewGuid();
    view.ReplaceAll(
    [
        new ToolTranscriptBlock(
            toolId,
            "grep",
            "{}",
            2,
            "hit",
            IsError: false,
            Complete: true),
    ]);

    view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonPressed,
        Position = new System.Drawing.Point(1, 0),
    });
    view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonReleased,
        Position = new System.Drawing.Point(1, 0),
    });
    Assert.True(view.IsExpanded(toolId));
    Assert.False(view.HasSelection);

    var diffId = Guid.NewGuid();
    view.ReplaceAll([new DiffTranscriptBlock(diffId, "@@ -1 +1 @@")]);
    view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonPressed,
        Position = new System.Drawing.Point(1, 0),
    });
    view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonReleased,
        Position = new System.Drawing.Point(1, 0),
    });
    Assert.True(view.IsExpanded(diffId));
    Assert.False(view.HasSelection);

    view.ReplaceAll(
    [
        new ToolTranscriptBlock(
            toolId,
            "grep",
            "{}",
            2,
            "hit",
            IsError: false,
            Complete: true),
    ]);
    view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonPressed,
        Position = new System.Drawing.Point(1, 0),
    });
    view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.PositionReport,
        Position = new System.Drawing.Point(4, 0),
    });
    view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonReleased,
        Position = new System.Drawing.Point(4, 0),
    });

    Assert.True(view.HasSelection);
    Assert.Equal("rep", view.GetSelectedText());
}

[Fact]
public void Selection_spans_rows_and_survives_scroll_and_redraw()
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(20, 10);
    var view = CreateView(app, out _);
    view.ReplaceAll(Blocks(30));
    view.BeginSelection(new TranscriptCellPosition(20, 2));
    view.UpdateSelection(new TranscriptCellPosition(22, 4));
    var selected = view.GetSelectedText();

    view.ScrollBy(-5);
    view.SetNeedsDraw();

    Assert.True(view.HasSelection);
    Assert.Equal(selected, view.GetSelectedText());
}

[Fact]
public void No_mouse_and_shift_drag_bypass_in_app_selection()
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    var view = CreateView(app, out _);
    view.ReplaceAll(Blocks(5));

    app.Mouse.IsMouseDisabled = true;
    Assert.False(view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonPressed,
        Position = new System.Drawing.Point(0, 0),
    }));

    app.Mouse.IsMouseDisabled = false;
    Assert.False(view.ProcessMouse(new Mouse
    {
        Flags = MouseFlags.LeftButtonPressed | MouseFlags.Shift,
        Position = new System.Drawing.Point(0, 0),
    }));
    Assert.False(view.HasSelection);
}
```

Add a drawing test with the ANSI driver that calls `app.Begin`, `LayoutAndDraw`, selects cells, calls `LayoutAndDraw` again, and asserts `view.SelectionDrawCount > 0`; this verifies the highlight path without inspecting unpublished driver internals.

- [ ] **Step 2: Write failing shell clipboard/arbitration tests**

```csharp
[Fact]
public void Ctrl_c_with_selection_copies_clears_and_does_not_arm_exit()
{
    string? copied = null;
    using var fixture = RetainedShellFixture.Create(
        activeWork: false,
        clipboardWriter: text =>
        {
            copied = text;
            return true;
        });
    fixture.Shell.Transcript.ReplaceAll(Lines(3));
    fixture.Shell.Transcript.BeginSelection(new TranscriptCellPosition(0, 0));
    fixture.Shell.Transcript.UpdateSelection(new TranscriptCellPosition(1, 4));
    fixture.Shell.Composer.SetFocus();

    fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

    Assert.NotNull(copied);
    Assert.False(fixture.Shell.Transcript.HasSelection);
    Assert.DoesNotContain("Press Ctrl+C again", fixture.Shell.Operational.Status.Text);
    Assert.Empty(fixture.Actions);
}

[Fact]
public void Clipboard_unavailable_keeps_selection_and_reports_status()
{
    Func<bool>? timeout = null;
    using var fixture = RetainedShellFixture.Create(
        activeWork: false,
        clipboardWriter: _ => false,
        addTimeout: (_, callback) =>
        {
            timeout = callback;
            return new object();
        },
        removeTimeout: _ => true);
    fixture.Shell.Transcript.ReplaceAll(Lines(3));
    fixture.Shell.Transcript.BeginSelection(new TranscriptCellPosition(0, 0));
    fixture.Shell.Transcript.UpdateSelection(new TranscriptCellPosition(0, 4));

    fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

    Assert.True(fixture.Shell.Transcript.HasSelection);
    Assert.Equal("Clipboard unavailable", fixture.Shell.Operational.Status.Text);
    Assert.Empty(fixture.Actions);

    Assert.False(timeout!());
    Assert.True(fixture.Shell.Transcript.HasSelection);
    Assert.Equal(
        OperationalStatusProjector.Project(fixture.Shell.Snapshot),
        fixture.Shell.Operational.Status);
}

[Fact]
public void Escape_clears_selection_before_arming_interrupt()
{
    using var fixture = RetainedShellFixture.Create(activeWork: true);
    fixture.Shell.Transcript.ReplaceAll(Lines(3));
    fixture.Shell.Transcript.BeginSelection(new TranscriptCellPosition(0, 0));
    fixture.Shell.Transcript.UpdateSelection(new TranscriptCellPosition(0, 4));

    fixture.Shell.Composer.NewKeyDownEvent(Key.Esc);

    Assert.False(fixture.Shell.Transcript.HasSelection);
    Assert.DoesNotContain("Press Esc again", fixture.Shell.Operational.Status.Text);
    Assert.Empty(fixture.Actions);
}
```

- [ ] **Step 3: Run selection/shell tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~VirtualizedTranscriptViewTests|FullyQualifiedName~FullscreenTuiShellTests" --no-restore
```

Expected: FAIL because drag state, selected text/highlight accessors, clipboard routing, and clipboard-unavailable override do not exist.

- [ ] **Step 4: Implement drag state and selected-cell drawing**

Add to `VirtualizedTranscriptView`:

```csharp
private readonly TranscriptSelection selection = new();
private bool dragging;
private TranscriptCellPosition pressPosition;

internal bool HasSelection => this.selection.HasSelection;
internal int SelectionDrawCount { get; private set; }

internal void BeginSelection(TranscriptCellPosition position)
{
    this.selection.Begin(position);
    this.pressPosition = position;
    this.dragging = true;
}

internal void UpdateSelection(TranscriptCellPosition position)
{
    this.selection.Update(position);
    this.SetNeedsDraw();
}

internal void ClearSelection()
{
    this.selection.Clear();
    this.dragging = false;
    this.SetNeedsDraw();
}

internal string GetSelectedText()
{
    if (!this.selection.HasSelection)
    {
        return string.Empty;
    }

    var ordered = this.selection.Ordered();
    var rows = this.index.GetRows(
        ordered.Start.GlobalRow,
        ordered.End.GlobalRow - ordered.Start.GlobalRow + 1);
    return this.selection.CopyText(rows);
}
```

Set `MousePositionTracking = true`. Handle mouse in this order:

```csharp
if (this.app.Mouse?.IsMouseDisabled == true ||
    mouse.Flags.HasFlag(MouseFlags.Shift))
{
    return false;
}

if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed))
{
    var position = this.ToTranscriptPosition(mouse);
    this.BeginSelection(position);
    this.app.Mouse.GrabMouse(this);
    return true;
}

if (this.dragging &&
    (mouse.Flags.HasFlag(MouseFlags.PositionReport) ||
     mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed)))
{
    var position = this.ToTranscriptPosition(mouse);
    if (position != this.pressPosition)
    {
        this.UpdateSelection(position);
    }

    return true;
}

if (this.dragging && mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased))
{
    var position = this.ToTranscriptPosition(mouse);
    this.app.Mouse.UngrabMouse();
    this.dragging = false;
    if (!this.selection.HasSelection)
    {
        this.ToggleExpansionAt(position.GlobalRow);
    }

    return true;
}

private TranscriptCellPosition ToTranscriptPosition(Mouse mouse)
{
    var local = mouse.Position ?? System.Drawing.Point.Empty;
    var globalRow = Math.Clamp(
        this.viewport.TopRow + Math.Max(0, local.Y),
        0,
        Math.Max(0, this.index.TotalRows - 1));
    return new TranscriptCellPosition(globalRow, Math.Max(0, local.X));
}

private void ToggleExpansionAt(int globalRow)
{
    if (this.index.BlockIdAt(globalRow) is not { } id)
    {
        return;
    }

    this.selectedBlockId = id;
    this.ToggleExpansion(id);
}
```

Keep wheel handling before drag handling. `ToTranscriptPosition` maps local Y through `viewport.TopRow` and clamps X to non-negative cells. Zero movement releases into the existing click expansion path. `--no-mouse` and Shift events return false before changing state.

Replace the direct `SetAttribute`/`AddStr(row.Text)` draw with:

```csharp
private void DrawRow(TranscriptRow row, int screenRow)
{
    var rowWidth = TerminalCellText.Width(row.Text);
    var range = this.selection.RangeForRow(row.GlobalRow, rowWidth);
    if (range is null)
    {
        this.SetAttribute(this.AttributeFor(row.Role));
        this.Move(0, screenRow);
        this.AddStr(row.Text);
        return;
    }

    var useTrueColor = TuiTheme.SupportsTrueColor(this.app.Driver);
    var selectedAttribute = new TgAttribute(
        TuiTheme.Resolve(this.theme.SelectionText, useTrueColor),
        TuiTheme.Resolve(this.theme.SelectionBackground, useTrueColor));
    var prefix = TerminalCellText.SliceByCells(row.Text, 0, range.Value.StartCell);
    var selected = TerminalCellText.SliceByCells(
        row.Text,
        range.Value.StartCell,
        range.Value.EndCellExclusive);
    var suffix = TerminalCellText.SliceByCells(
        row.Text,
        range.Value.EndCellExclusive,
        rowWidth);

    this.SetAttribute(this.AttributeFor(row.Role));
    this.Move(0, screenRow);
    this.AddStr(prefix);
    var column = TerminalCellText.Width(prefix);
    if (selected.Length > 0)
    {
        this.SetAttribute(selectedAttribute);
        this.Move(column, screenRow);
        this.AddStr(selected);
        column += TerminalCellText.Width(selected);
        this.SelectionDrawCount++;
    }

    this.SetAttribute(this.AttributeFor(row.Role));
    this.Move(column, screenRow);
    this.AddStr(suffix);
}
```

Call `DrawRow(row, screenRow)` from `OnDrawingContent`. Do not alter `CollectVisibleRows`; selection remains visible only where its global rows intersect the viewport.

- [ ] **Step 5: Implement clipboard arbitration through the public API**

In `TerminalGuiShellBase`, initialize:

```csharp
this.clipboardWriter = clipboardWriter ??
    (text => this.app.Clipboard?.TrySetClipboardData(text) == true);
```

Implement:

```csharp
private bool TryClearTranscriptSelection()
{
    if (!this.TranscriptView.HasSelection)
    {
        return false;
    }

    this.TranscriptView.ClearSelection();
    this.RestoreProjectedOperationalStatus();
    return true;
}

private bool TryCopyTranscriptSelection()
{
    if (!this.TranscriptView.HasSelection)
    {
        return false;
    }

    var text = this.TranscriptView.GetSelectedText();
    if (!string.IsNullOrEmpty(text) && this.clipboardWriter(text))
    {
        this.TranscriptView.ClearSelection();
        this.RestoreProjectedOperationalStatus();
        return true;
    }

    this.ShowTransientOperationalStatus(
        new OperationalStatus("Clipboard unavailable", OperationalTone.Warning, false),
        TimeSpan.FromSeconds(1.5));
    return true;
}

private void ShowTransientOperationalStatus(
    OperationalStatus status,
    TimeSpan duration)
{
    this.ClearTransientOperationalOverride();
    this.transientOperationalOverride = status;
    this.Operational.SetStatus(status);
    this.transientOperationalTimeout = this.addTimeout(
        duration,
        () =>
        {
            this.transientOperationalTimeout = null;
            if (this.disposed)
            {
                return false;
            }

            this.transientOperationalOverride = null;
            this.RestoreProjectedOperationalStatus();
            return false;
        });
}

private void ClearTransientOperationalOverride()
{
    this.transientOperationalOverride = null;
    if (this.transientOperationalTimeout is not { } token)
    {
        return;
    }

    this.transientOperationalTimeout = null;
    this.removeTimeout(token);
}

private bool TryClearTransientOperationalOverride()
{
    if (this.transientOperationalOverride is null)
    {
        return false;
    }

    this.ClearTransientOperationalOverride();
    this.RestoreProjectedOperationalStatus();
    return true;
}
```

Expose the concrete transcript to the base as:

```csharp
// TerminalGuiShellBase
protected abstract VirtualizedTranscriptView TranscriptView { get; }

// FullscreenTuiShell (inherited by InlineTuiShell)
protected override VirtualizedTranscriptView TranscriptView => this.transcript;
```

Use `TranscriptView` consistently in focus, selection, clipboard, chord, and disposal code. The final implementation must call the public Terminal.Gui clipboard API only in the default delegate.

Add `private object? transientOperationalTimeout;` beside the chord timeout field.

- [ ] **Step 6: Run selection/shell tests and verify green**

Run the Step 3 command again.

Expected: PASS. Drag selection spans rows, survives redraw/scroll, highlights through Warm Ember, zero movement still expands, Ctrl+C copies before chord handling, unavailable clipboard preserves selection, Escape clears first, and mouse-disabled/Shift drag bypass in-app selection.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\VirtualizedTranscriptView.cs src\Coda.Tui\Ui\Shells\TerminalGuiShellBase.cs src\Coda.Tui\Ui\Shells\FullscreenTuiShell.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs
git commit -m "feat(tui): select and copy transcript text" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 13: Lock z-order, timer cleanup, and retained/plain regressions

**Files:**
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/InlineTuiShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/CommandCompletionShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/TerminalGuiModeRunnerTests.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`

- [ ] **Step 1: Write prompt/completion/status z-order tests**

Add to `CommandCompletionShellTests.cs`:

```csharp
[Fact]
public async Task Completion_operational_composer_metadata_and_prompt_keep_final_z_order()
{
    using IApplication app = Application.Create();
    app.AppModel = AppModel.FullScreen;
    app.Init(DriverRegistry.Names.ANSI);
    app.Driver!.SetScreenSize(80, 24);
    using var shell = ShellTestFactory.CreateFullscreen(app, Commands());
    var token = app.Begin(shell);
    app.LayoutAndDraw();
    shell.Composer.SetDraft("/m", 2);
    var prompt = UiPromptRequest.Confirm("Allow?", false);

    await shell.ApplyAsync(
        UiSessionSnapshot.Empty with { PendingPrompt = prompt },
        CancellationToken.None);
    app.LayoutAndDraw();

    Assert.True(shell.Completion.Visible);
    Assert.True(shell.PromptOverlay.Visible);
    Assert.Equal(shell.Operational.Frame.Y, shell.Completion.Frame.Bottom);
    Assert.Equal(shell.Composer.Frame.Y, shell.Operational.Frame.Bottom);
    Assert.Equal(shell.Status.Frame.Y, shell.Composer.Frame.Bottom);

    var order = shell.SubViews.ToList();
    Assert.True(order.IndexOf(shell.Chrome) < order.IndexOf(shell.Composer));
    Assert.True(order.IndexOf(shell.Completion) < order.IndexOf(shell.PromptOverlay));
    Assert.Equal(order.Count - 1, order.IndexOf(shell.PromptOverlay));
    Assert.True(shell.PromptOverlay.HasFocus);
    Assert.False(shell.Composer.HasFocus);

    if (token is not null)
    {
        app.End(token);
    }
}
```

Add the same row adjacency assertions to inline mode after a dynamic-height draft and after driver resize.

- [ ] **Step 2: Write status-override restoration and disposal tests**

```csharp
[Fact]
public void Expired_chord_hint_restores_projected_status()
{
    var clock = new ManualTimeProvider();
    Func<bool>? timeout = null;
    using var fixture = RetainedShellFixture.Create(
        activeWork: true,
        timeProvider: clock,
        addTimeout: (_, callback) =>
        {
            timeout = callback;
            return new object();
        });
    fixture.Shell.Composer.NewKeyDownEvent(Key.Esc);
    Assert.Equal("Press Esc again to interrupt", fixture.Shell.Operational.Status.Text);

    clock.Advance(TimeSpan.FromMilliseconds(801));
    Assert.False(timeout!());

    Assert.NotEqual("Press Esc again to interrupt", fixture.Shell.Operational.Status.Text);
    Assert.Equal(
        OperationalStatusProjector.Project(fixture.Shell.Snapshot),
        fixture.Shell.Operational.Status);
}

[Fact]
public void Disposing_shell_removes_spinner_and_chord_timeouts()
{
    var removed = 0;
    var fixture = RetainedShellFixture.Create(
        activeWork: true,
        removeTimeout: _ =>
        {
            removed++;
            return true;
        });
    fixture.Shell.Composer.NewKeyDownEvent(Key.Esc);
    fixture.Shell.ApplyAsync(
        UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("turn", "working", null),
        },
        CancellationToken.None).GetAwaiter().GetResult();

    fixture.Dispose();

    Assert.True(removed >= 2, "spinner and chord timeout must both be removed");
}
```

The shared `RetainedShellFixture` from Task 10 passes add/remove timeout delegates into the final shell constructor. Production defaults still call `IApplication.AddTimeout`/`RemoveTimeout`.

- [ ] **Step 3: Strengthen plain/Spectre non-construction tests**

In `TerminalGuiModeRunnerTests.cs`, add:

```csharp
[Theory]
[InlineData(TuiRunMode.Plain)]
[InlineData(TuiRunMode.Spectre)]
public async Task Non_terminal_modes_never_create_an_application_or_retained_state(TuiRunMode mode)
{
    var applicationFactoryCalls = 0;
    var shellFactoryCalls = 0;
    var runner = new TerminalGuiModeRunner(
        shellFactory: (_, _, _) =>
        {
            shellFactoryCalls++;
            throw new InvalidOperationException("retained shell must not be created");
        },
        spectreRunner: (_, _) => Task.FromResult(TuiShellExit.Exited),
        plainRunner: (_, _) => Task.FromResult(TuiShellExit.Exited),
        applicationFactory: () =>
        {
            applicationFactoryCalls++;
            return Application.Create();
        });

    var result = await runner.RunAsync(mode, ComposerState.Empty, CancellationToken.None);

    Assert.Equal(TuiShellExitKind.Exit, result.Kind);
    Assert.Equal(0, applicationFactoryCalls);
    Assert.Equal(0, shellFactoryCalls);
}
```

- [ ] **Step 4: Run the regression tests and verify red if lifecycle gaps remain**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~CommandCompletionShellTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests|FullyQualifiedName~TerminalGuiModeRunnerTests" --no-restore
```

Expected: PASS only when prompt order, override expiry, resize anchors, and timeout disposal all match the exact assertions. A failure identifies the corresponding lifecycle line to correct in Step 5.

- [ ] **Step 5: Make the minimal lifecycle corrections**

Ensure `TerminalGuiShellBase.Dispose` performs this order once:

```csharp
this.ResetChordOverride();
this.ClearTransientOperationalOverride();
this.Composer.ShellKeyHandler = null;
this.Composer.LayoutInvalidated -= this.OnComposerLayoutInvalidated;
this.Initialized -= this.OnShellInitialized;
this.Composer.Submitted -= this.OnComposerSubmitted;
this.Composer.ActionRequested -= this.OnComposerActionRequested;
this.Composer.CompletionChanged -= this.OnCompletionChanged;
this.UnbindTranscriptInput(this.TranscriptView);
```

Then call `base.Dispose(disposing)` so child `OperationalStatusView.Dispose` removes its spinner. Prompt activation calls `ResetChordOverride` before setting prompt focus. Mode switch calls it before raising `UiAction.ToggleMode`. `RequestStop` clears transient/chord timers before `app.RequestStop()`. No timer callback may draw when `disposed` or `!app.Initialized`.

Keep view insertion order exactly as Task 5 and call `PromptOverlay.SetFocus()` after every pending-prompt update so completion can remain visible underneath without receiving input.

- [ ] **Step 6: Run regression tests and verify green**

Run the Step 4 command again.

Expected: PASS in inline and full-screen modes; plain/Spectre create no retained application/view state.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\TerminalGuiShellBase.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs tests\Coda.Tui.Tests\InlineTuiShellTests.cs tests\Coda.Tui.Tests\CommandCompletionShellTests.cs tests\Coda.Tui.Tests\TerminalGuiModeRunnerTests.cs
git commit -m "test(tui): lock retained shell layering and lifecycle" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 14: Update help, README, PTY guidance, and compatibility wording

**Files:**
- Modify: `src/Coda.Tui/ImmediateCli.cs:53-63`
- Modify: `tests/Coda.Tui.Tests/ImmediateCliTests.cs`
- Modify: `README.md:96-128`
- Modify: `docs/terminal-gui-compatibility.md:28-117`
- Modify: `scripts/terminal-gui-pty-smoke.ps1:54-79`
- Modify: `samples/Coda.TerminalGuiSpike/HarnessOptions.cs:163-195`
- Modify: `samples/Coda.TerminalGuiSpike/SpikeHarness.cs:271-303`

- [ ] **Step 1: Write failing help assertions**

Add to `ImmediateCliTests.cs`:

```csharp
[Fact]
public void Help_documents_dynamic_composer_history_chords_and_copy()
{
    var (_, output) = Run("--help");

    Assert.Contains("Ctrl+J inserts a newline", output, StringComparison.Ordinal);
    Assert.Contains("Ctrl+Up/Ctrl+Down navigate prompt history", output, StringComparison.Ordinal);
    Assert.Contains("Esc twice interrupts active work", output, StringComparison.Ordinal);
    Assert.Contains("Ctrl+C twice exits", output, StringComparison.Ordinal);
    Assert.Contains("drag selects transcript text", output, StringComparison.Ordinal);
    Assert.Contains("Shift+drag", output, StringComparison.Ordinal);
    Assert.Contains("/exit", output, StringComparison.Ordinal);
    Assert.DoesNotContain("Ctrl+D", output, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run help tests and verify red**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ImmediateCliTests" --no-restore
```

Expected: FAIL because immediate help has no key section and still omits the approved chord/selection behavior.

- [ ] **Step 3: Add exact immediate-help text**

After the `--no-mouse` option in `ImmediateCli.WriteUsage`, add:

```csharp
writer.WriteLine();
writer.WriteLine("Interactive keys:");
writer.WriteLine("  Enter submits; Ctrl+J inserts a newline; Up/Down moves through wrapped");
writer.WriteLine("  composer lines (or completion choices), and Ctrl+Up/Ctrl+Down navigate prompt history.");
writer.WriteLine("  Esc dismisses completion/selection first; Esc twice interrupts active work.");
writer.WriteLine("  Ctrl+C copies selected transcript text, otherwise Ctrl+C twice exits; /exit also exits.");
writer.WriteLine("  With mouse enabled, left drag selects transcript text; Shift+drag uses native terminal");
writer.WriteLine("  selection where supported. --no-mouse leaves selection/copy to the terminal.");
```

Do not mention Ctrl+D.

- [ ] **Step 4: Replace the README retained-TUI description with approved behavior**

Use this exact keys/layout paragraph:

```markdown
The retained Terminal.Gui surface uses the **Warm Ember** palette on a near-black background: assistant
text is warm ivory/peach, user turns are amber, tools are muted gold, approvals are coral, questions and
warnings are warm yellow, and errors use restrained red. The transcript uses the full terminal width and
remains virtualized in both full-screen and inline modes. A dedicated operational row sits immediately
above the borderless composer; stable model/effort/context/token/cost/MCP/LSP/git/cwd metadata remains on
the final row.

The composer starts at three rows, grows with explicit and visual wraps up to
`max(3, min(8, floor(screen height × 0.35)))`, then scrolls internally while keeping the caret visible.
It receives focus when startup becomes ready, after prompts close, and after inline/full-screen switches.

**Keys:** `Enter` submits · `Ctrl+J` inserts a newline · completion-open `Up`/`Down` changes the selection ·
otherwise `Up`/`Down` moves by visual wrapped line and crosses into history at the boundary ·
`Ctrl+Up`/`Ctrl+Down` explicitly navigates history · `Tab` completes · `Esc` dismisses completion or
selection, then double-`Esc` within 800 ms interrupts active work · `Ctrl+C` copies selected transcript
text, otherwise double-`Ctrl+C` within 1.5 s exits · `/exit` exits · `F2` switches retained modes.

With mouse support enabled, left-button drag selects transcript text and `Ctrl+C` copies it. A click with
no movement keeps tool/diff expand/collapse behavior. `--no-mouse` disables in-app selection; Shift+drag
remains the native-terminal selection escape hatch where the terminal supports bypassing mouse reporting.
```

Keep the existing mode-selection and plain-mode paragraphs. Remove the old `Ctrl-C interrupts` and `Ctrl+D` wording.

- [ ] **Step 5: Update compatibility checklist and operator script**

In `docs/terminal-gui-compatibility.md`, make the acceptance bullets include:

```markdown
- **Warm Ember:** truecolor terminals show distinct assistant/user/tool/approval/question/error roles;
  forced/naturally low-color terminals use readable named 16-color fallbacks with no primary blue/magenta
  tool/approval roles.
- **Composer/cursor:** startup shows only the operational `Initializing…` row; composer chrome is blank and
  the cursor is hidden until ready. The composer grows to its cap, then scrolls with the caret visible.
- **Selection/copy:** left drag selects across wrapped rows/blocks, zero-movement click still expands,
  Ctrl+C copies and clears, unavailable clipboard preserves selection, `--no-mouse` bypasses selection,
  and Shift+drag provides native fallback where supported.
- **Chords:** first Esc/Ctrl+C shows the operational hint; the second key within its documented window acts;
  expiry, prompts, mode switches, and disposal restore the projected status.
```

Rename checklist item 9 to `Selection/copy`, item 13 to `Exit/interrupt chords`, and update scenario mapping so `cancel` verifies both chord windows and `mouse-off` verifies in-app selection bypass. Keep all cells `☐ untested`; documentation must not claim a manual result.

In `scripts/terminal-gui-pty-smoke.ps1`, use:

```powershell
'cancel' = 'Esc twice within 800ms interrupts active work; Ctrl+C twice within 1.5s exits; hints expire back to projected status without terminal corruption.'
'mouse-off' = '--no-mouse bypasses in-app drag selection; keyboard navigation/editing remain usable and Shift+drag native selection is available where supported.'
```

Change launch text to:

```powershell
Write-Host 'Launching the spike (interact with it, then use the scenario-specific exit chord or /exit)...' -ForegroundColor Green
```

In `HarnessOptions.HelpText` and `SpikeHarness.RunCancelHeadless`, replace the old "Ctrl-C interrupts, explicit exit" wording with:

```text
cancel        Exercise operational hints: double-Esc interrupts and double-Ctrl+C exits.
```

and:

```csharp
ui.AppendTranscript("Cancel sample: double-Esc interrupts active work; double-Ctrl+C exits.");
ui.SetStatus("Verify both operational hints and their timeout reset; /exit remains explicit.");
```

The compatibility spike remains guidance/harness text; do not duplicate production chord state there.

- [ ] **Step 6: Run documentation-coupled tests**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ImmediateCliTests|FullyQualifiedName~HelpCoverageTests|FullyQualifiedName~CommandHelpParityTests" --no-restore
dotnet build samples\Coda.TerminalGuiSpike\Coda.TerminalGuiSpike.csproj --no-restore
```

Expected: both commands PASS. No test claims a PTY/manual color, cursor, or mouse observation.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Tui\ImmediateCli.cs tests\Coda.Tui.Tests\ImmediateCliTests.cs README.md docs\terminal-gui-compatibility.md scripts\terminal-gui-pty-smoke.ps1 samples\Coda.TerminalGuiSpike\HarnessOptions.cs samples\Coda.TerminalGuiSpike\SpikeHarness.cs
git commit -m "docs(tui): document Warm Ember interaction model" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 15: Run full automated gates and the manual PTY checklist

**Files:**
- Verify only. If a gate fails, return to the originating implementation task, add the failing regression there, fix it, recommit, and rerun this task. Never modify version/package files here.

- [ ] **Step 1: Run the complete TUI suite**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --no-restore
```

Expected: PASS, 0 failed, including theme/fallback, operational projection/timer, full-width virtualization, dynamic composer, laid-out caret navigation, focus, chords, selection/clipboard, inline/full-screen lifecycle, help, and plain/Spectre regressions.

- [ ] **Step 2: Run engine and authentication regressions**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --no-restore
dotnet test tests\LlmAuth.Tests\LlmAuth.Tests.csproj --no-restore
```

Expected: both PASS, 0 failed. Presentation work must not change engine/tool execution or authentication.

- [ ] **Step 3: Build the solution**

Run:

```powershell
dotnet build LlmAuth.slnx --no-restore
```

Expected: BUILD SUCCEEDED with 0 errors.

- [ ] **Step 4: Run architecture searches**

Run:

```powershell
rg -n "MaximumTranscriptWidth|Pos\.Center\(\)" src\Coda.Tui\Ui\Shells
rg -n "new TgColor|ColorName16|new Terminal\.Gui\.Drawing\.Color" src\Coda.Tui\Ui\Shells src\Coda.Tui\Ui\Input
rg -n "ActiveOperation|FormatPermission|snapshot\.Permission" src\Coda.Tui\Ui\State\StatusProjector.cs
rg -n "Key\.D\.WithCtrl|IdleNotification|Ctrl\+D" src\Coda.Tui README.md docs\terminal-gui-compatibility.md scripts\terminal-gui-pty-smoke.ps1 samples\Coda.TerminalGuiSpike
rg -n "FakeDriver|Activator|BindingFlags|GetProperty\(" tests\Coda.Tui.Tests\TuiThemeTests.cs tests\Coda.Tui.Tests\OperationalStatusViewTests.cs tests\Coda.Tui.Tests\ComposerViewTests.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs tests\Coda.Tui.Tests\InlineTuiShellTests.cs
rg -n "OperationalStatus|TranscriptSelection|ShellCommandChordState|ScrollRow|PreferredDisplayColumn" src\Coda.Tui\Ui\State\UiSessionSnapshot.cs
rg -n "Height = 3|AnchorEnd\(4\)|Fill\(4\)" src\Coda.Tui\Ui\Shells
rg -n 'PackageReference Include="Terminal.Gui" Version="2\.4\.17"' src\Coda.Tui\Coda.Tui.csproj
git diff --exit-code cf00b66..HEAD -- version.json version.props
```

Expected:

- the first seven searches return no matches;
- the Terminal.Gui package search returns exactly the existing 2.4.17 reference;
- the version diff exits 0, proving no version bump;
- any unexpected match must be removed or justified by narrowing the search to a test string, never by weakening the architecture.

- [ ] **Step 5: Inspect the final change set**

Run:

```powershell
git status --short
git --no-pager diff --check
git --no-pager log --oneline cf00b66..HEAD
```

Expected: clean working tree after the task commits, no whitespace errors, and frequent focused commits corresponding to Tasks 1-14.

- [ ] **Step 6: Execute and record the manual PTY checklist without treating it as automated**

Run each retained mode in a real supported terminal:

```powershell
dotnet run --project src\Coda.Tui -- --tui=fullscreen
dotnet run --project src\Coda.Tui -- --tui=inline
dotnet run --project src\Coda.Tui -- --tui=fullscreen --no-mouse
```

Also run the existing operator harness for both host models:

```powershell
.\scripts\terminal-gui-pty-smoke.ps1 -TerminalName "Windows Terminal" -Mode fullscreen -Scenario unicode
.\scripts\terminal-gui-pty-smoke.ps1 -TerminalName "Windows Terminal" -Mode fullscreen -Scenario resize
.\scripts\terminal-gui-pty-smoke.ps1 -TerminalName "Windows Terminal" -Mode fullscreen -Scenario cancel
.\scripts\terminal-gui-pty-smoke.ps1 -TerminalName "Windows Terminal" -Mode fullscreen -Scenario mouse-off
.\scripts\terminal-gui-pty-smoke.ps1 -TerminalName "Windows Terminal" -Mode inline -Scenario unicode
.\scripts\terminal-gui-pty-smoke.ps1 -TerminalName "Windows Terminal" -Mode inline -Scenario resize
.\scripts\terminal-gui-pty-smoke.ps1 -TerminalName "Windows Terminal" -Mode inline -Scenario cancel
.\scripts\terminal-gui-pty-smoke.ps1 -TerminalName "Windows Terminal" -Mode inline -Scenario mouse-off
```

Manually observe and record, without converting the observations into automated claims:

1. assistant ivory/peach, user amber, tool muted gold, approval coral, question/warning yellow, restrained error red, and readable named-color fallback in a low-color terminal;
2. startup banner plus operational `Initializing…`, blank composer chrome, hidden cursor, then ready `>` and immediate typing focus;
3. full-width transcript and bounded responsive scrolling before/after resize;
4. composer growth from three rows to the formula cap, internal scroll, visible caret, paste/completion/history/mode restore;
5. completion immediately above operational status and prompt overlay topmost;
6. printable transcript-focus typing redirects while PageUp/PageDown/arrows remain transcript navigation;
7. first/second Esc and Ctrl+C hints/actions plus timeout reset;
8. left drag highlight across wrapped rows/blocks, persistent selection while scrolling, Ctrl+C clipboard copy, zero-movement tool/diff click, unavailable clipboard preservation;
9. `--no-mouse` bypass and Shift+drag native selection fallback where Windows Terminal supports it;
10. inline/full-screen exit restores cursor, mouse mode, primary/alternate buffer, and terminal usability.

Expected: the operator records Pass/Fail/Skip in the existing compatibility evidence flow. Do not state that these PTY observations passed unless they were actually performed.

---

## Plan self-review

### Spec coverage

| Approved requirement | Covered by |
|---|---|
| Central Warm Ember RGB + named fallbacks; all retained semantic surfaces | Tasks 1-2, 4-5 |
| Full-width transcript with virtualization/incremental resize performance | Task 3, final gates |
| Dedicated always-visible operational model/projector/view and metadata split | Tasks 4-5 |
| Correct status priority, effort tones, incomplete tool, spinner lifecycle, local overrides | Tasks 4-5, 10, 13 |
| Borderless/no-accent composer; blank startup chrome/cursor hidden | Tasks 2, 5 |
| Dynamic 3-to-cap composer, grapheme/cell wrap, internal scroll, all recalculation triggers | Tasks 6-7 |
| Completion/editor/history multiline navigation and laid-out caret tests | Tasks 6-8 |
| Startup/prompt/mode focus and type-anywhere routing with modal protection | Task 9 |
| Esc/Ctrl+C monotonic chords, timeout/reset, `/exit`, no Ctrl+D | Task 10 |
| Global-row transcript drag selection, highlight, clipboard, zero-click expansion, no-mouse/Shift bypass | Tasks 11-12 |
| Z-order, prompt/completion coexistence, plain/Spectre regression | Task 13 |
| README/help/compatibility/manual PTY updates | Tasks 14-15 |
| No version bump/local install | Global constraints and Task 15 searches |

### Type consistency

- `OperationalTone.Warning` is defined before chord/clipboard tasks and maps to `TuiTheme.Warning`.
- `ComposerState` uses the same seven positional members in controller, shell transfer, tests, and mode switching.
- `ComposerVisualLayout` is the sole UTF-16 ↔ visual-row mapping seam; `ComposerView` does not retain the old logical-line helpers.
- `TranscriptSelection` endpoints are inclusive cell positions; `RangeForRow` returns half-open ranges and `CopyText` consumes those ranges.
- `TerminalGuiShellBase` owns one attached `TranscriptView`, one `ShellCommandChordState`, and one local operational override path.
- `Status` consistently means stable bottom metadata; `Operational` consistently means the dedicated row.

### Placeholder scan

- No deferred marker, unspecified error handling, fake-driver workaround, reflection workaround, packaging step, local install step, or version bump is part of this plan.
- Every implementation task names exact files, tests, commands, expected red/green result, signatures, behavior, and commit.
